using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using System.Text;

namespace ClassHub_API.Services
{
    public class MqttService : IMqttService
    {
        private const string mqttUrl = "5f6dd65ef73945c2832e7dd2d5f3f8c4.s1.eu.hivemq.cloud";
        private const int port = 8883;
        private readonly IMqttClient _mqttClient;
        private readonly IMqttClientOptions _mqttOptions;

        public MqttService()
        {
            var factory = new MqttFactory();
            _mqttClient = factory.CreateMqttClient();

            _mqttOptions = new MqttClientOptionsBuilder()
                .WithTcpServer(mqttUrl, port)
                .WithCredentials("esp32s3", "Abc@@123")
                .WithTls()
                .Build();
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