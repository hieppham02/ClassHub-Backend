using ClassHub_API.Data;
using Newtonsoft.Json;

namespace ClassHub_API.Services
{
    public class CabinetStatusPayload
    {
        public string id { get; set; }
        public string room { get; set; }
        public int isOpen { get; set; }
    }
    public class BackgroundServices : BackgroundService
    {
        private readonly IMqttService _mqttService;
        private readonly IServiceScopeFactory _scopeFactory;

        public BackgroundServices(IMqttService mqttService, IServiceScopeFactory scopeFactory)
        {
            _mqttService = mqttService;
            _scopeFactory = scopeFactory;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(2000, stoppingToken);

            await _mqttService.SubscribeAsync("tu_thiet_bi/STATUS", async (topic, payload) =>
            {
                Console.WriteLine($"[MQTT Received] Topic: {topic} | Payload: {payload}");

                try
                {
                    var data = JsonConvert.DeserializeObject<CabinetStatusPayload>(payload);
                    if (data != null && int.TryParse(data.id, out int phieuId))
                    {
                        using (var scope = _scopeFactory.CreateScope())
                        {
                            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                            var phieu = dbContext.phieu_muon.FirstOrDefault(p => p.id == phieuId && p.ma_phong == data.room);

                            if (phieu != null)
                            {
                                phieu.is_cabinet_open = data.isOpen;

                                if (phieu.trang_thai == "PENDING" && data.isOpen == 1)
                                {
                                    phieu.trang_thai = "IN_USE";
                                }

                                await dbContext.SaveChangesAsync();
                                Console.WriteLine($"[DB] Đã cập nhật Tủ {data.room} -> is_cabinet_open = {data.isOpen}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Lỗi xử lý MQTT Database: " + ex.Message);
                }
            });
        }
    }
}
