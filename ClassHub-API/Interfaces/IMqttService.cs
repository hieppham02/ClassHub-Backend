namespace ClassHub_API.Services
{
    public interface IMqttService
    {
        Task PublishAsync(string topic, string payload);
        Task SubscribeAsync(string topic, Action<string, string> onMessageReceived);
    }
}