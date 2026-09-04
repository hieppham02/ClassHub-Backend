using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using System.Collections.Concurrent;
using System.Text;

namespace ClassHub_API.Services
{
    public class MqttService : IMqttService
    {
        private readonly IMqttClient _mqttClient;
        private readonly IMqttClientOptions _mqttOptions;
        private readonly ILogger<MqttService> _logger;

        private readonly SemaphoreSlim _connectionLock = new(1, 1);

        private readonly ConcurrentDictionary<string, Action<string, string>> _subscriptions = new();

        private int _isReconnecting;

        public MqttService(IConfiguration configuration, ILogger<MqttService> logger)
        {
            _logger = logger;

            string brokerHost = GetRequiredConfiguration(configuration, "MqttSettings:BrokerHost");

            string username = GetRequiredConfiguration(configuration, "MqttSettings:Username");

            string password = GetRequiredConfiguration(configuration, "MqttSettings:Password");

            int port = configuration.GetValue<int?>("MqttSettings:Port") ?? 8883;

            string clientId = configuration["MqttSettings:ClientId"] ?? "classhub-backend";

            var factory = new MqttFactory(); 
            _mqttClient = factory.CreateMqttClient();
            _mqttOptions = new MqttClientOptionsBuilder()
                .WithClientId(clientId)
                .WithTcpServer(brokerHost, port)
                .WithCredentials(username, password)
                .WithTls()
                .WithCleanSession()
                .Build();

            ConfigureMessageHandler();
            ConfigureDisconnectedHandler();
        }

        public async Task PublishAsync(string topic, string payload)
        {
            if (string.IsNullOrWhiteSpace(topic))
            {
                throw new ArgumentException("MQTT topic không được để trống.", nameof(topic));
            }

            await EnsureConnectedAsync();

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithAtLeastOnceQoS()
                .Build();

            await _mqttClient.PublishAsync(message, CancellationToken.None);

            _logger.LogInformation("Đã publish MQTT message tới topic {Topic}", topic);
        }

        public async Task SubscribeAsync(string topic, Action<string, string> onMessageReceived)
        {
            if (string.IsNullOrWhiteSpace(topic))
            {
                throw new ArgumentException("MQTT topic không được để trống.", nameof(topic));
            }

            ArgumentNullException.ThrowIfNull(onMessageReceived);

            _subscriptions[topic] = onMessageReceived;

            await EnsureConnectedAsync();
            await SubscribeToTopicAsync(topic);

            _logger.LogInformation("Đã subscribe MQTT topic {Topic}", topic);
        }

        private async Task EnsureConnectedAsync()
        {
            if (_mqttClient.IsConnected)
            {
                return;
            }

            await _connectionLock.WaitAsync();

            try
            {
                if (_mqttClient.IsConnected)
                {
                    return;
                }

                _logger.LogInformation("Đang kết nối tới MQTT broker...");

                await _mqttClient.ConnectAsync( _mqttOptions,  CancellationToken.None);

                _logger.LogInformation("Kết nối MQTT broker thành công.");
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        private void ConfigureMessageHandler()
        {
            _mqttClient.UseApplicationMessageReceivedHandler(eventArgs =>
            {
                string receivedTopic = eventArgs.ApplicationMessage.Topic;

                string payload = Encoding.UTF8.GetString(eventArgs.ApplicationMessage.Payload);

                foreach (var subscription in _subscriptions)
                {
                    if (!TopicMatches( subscription.Key, receivedTopic))
                    {
                        continue;
                    }

                    try
                    {
                        subscription.Value(receivedTopic, payload);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogError(exception, "Lỗi xử lý MQTT message từ topic {Topic}", receivedTopic);
                    }
                }
            });
        }

        private void ConfigureDisconnectedHandler()
        {
            _mqttClient.UseDisconnectedHandler(async eventArgs =>
            {
                _logger.LogWarning(eventArgs.Exception, "Mất kết nối MQTT broker.");

                await ReconnectAsync();
            });
        }

        private async Task ReconnectAsync()
        {
            if (Interlocked.Exchange(ref _isReconnecting,1) == 1)
            {
                return;
            }

            try
            {
                while (!_mqttClient.IsConnected)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5));

                        await EnsureConnectedAsync();
                        await ResubscribeAsync();

                        _logger.LogInformation("Đã kết nối lại MQTT broker.");
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception,"Kết nối lại MQTT thất bại. Sẽ thử lại sau 5 giây.");
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _isReconnecting, 0);
            }
        }

        private async Task ResubscribeAsync()
        {
            foreach (string topic in _subscriptions.Keys)
            {
                await SubscribeToTopicAsync(topic);
            }
        }

        private async Task SubscribeToTopicAsync(string topic)
        {
            var topicFilter = new MqttTopicFilterBuilder()
                .WithTopic(topic)
                .WithAtLeastOnceQoS()
                .Build();

            await _mqttClient.SubscribeAsync(topicFilter);
        }

        private static bool TopicMatches(
            string filter,
            string topic)
        {
            string[] filterLevels = filter.Split('/');
            string[] topicLevels = topic.Split('/');

            for (int index = 0; index < filterLevels.Length; index++)
            {
                string filterLevel = filterLevels[index];

                if (filterLevel == "#")
                {
                    return index == filterLevels.Length - 1;
                }

                if (index >= topicLevels.Length)
                {
                    return false;
                }

                if (filterLevel != "+" && filterLevel != topicLevels[index])
                {
                    return false;
                }
            }

            return filterLevels.Length == topicLevels.Length;
        }

        private static string GetRequiredConfiguration(
            IConfiguration configuration,
            string key)
        {
            string? value = configuration[key];

            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Thiếu cấu hình bắt buộc: {key}. " + "Hãy kiểm tra User Secrets hoặc Environment Variables.");
            }

            return value;
        }
    }
}