using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using System.Text;

namespace ClassHub_API.Services
{
    public class MqttService : IMqttService
    {
        private readonly IMqttClient _mqttClient;
        private readonly IMqttClientOptions _mqttOptions;
        private readonly IConfiguration _configuration;

        public MqttService(IConfiguration configuration)
        {
            _configuration = configuration;

            string broker = _configuration["MqttSettings:BrokerHost"] ?? "5f6dd65ef73945c2832e7dd2d5f3f8c4.s1.eu.hivemq.cloud";
            int port = int.Parse(_configuration["MqttSettings:Port"] ?? "8883");
            string username = _configuration["MqttSettings:Username"] ?? "esp32s3";
            string password = _configuration["MqttSettings:Password"] ?? "Abc@@123";

            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient();

            _mqttOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(broker, port)
                .WithCredentials(username, password)
                .WithTls()
                .WithCleanSession()
                .Build();

            // Tự động kết nối lại khi đứt mạng
            _mqttClient.UseDisconnectedHandler(async e =>
            {
                Console.WriteLine("[MQTT] Bị mất kết nối broker, đang thử kết nối lại sau 5s...");
                await Task.Delay(TimeSpan.FromSeconds(5));
                try
                {
                    await _mqttClient.ConnectAsync(_mqttOptions, CancellationToken.None);
                    Console.WriteLine("[MQTT] Đã kết nối lại thành công!");
                }
                catch { }
            });
        }

        public async Task PublishAsync(string topic, string payload)
        {
            if (!_mqttClient.IsConnected)
            {
                await _mqttClient.ConnectAsync(_mqttOptions, CancellationToken.None);
            }

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithAtLeastOnceQoS()
                .Build();

            await _mqttClient.PublishAsync(message, CancellationToken.None);
            Console.WriteLine($"[MQTT Published] Topic: {topic} | Payload: {payload}");
        }

        public async Task SubscribeAsync(string topic, Action<string, string> onMessageReceived)
        {
            if (!_mqttClient.IsConnected)
            {
                await _mqttClient.ConnectAsync(_mqttOptions, CancellationToken.None);
            }

            await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(topic).Build());

            _mqttClient.UseApplicationMessageReceivedHandler(e =>
            {
                string receivedTopic = e.ApplicationMessage.Topic;
                string payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                onMessageReceived(receivedTopic, payload);
            });
        }
    }
}