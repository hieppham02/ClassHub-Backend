using ClassHub_API.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ClassHub_API.Services;
public class ReadinessCheck(IServiceScopeFactory scopes, IMqttService mqtt) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken token = default)
    {
        if (!mqtt.IsReady) return HealthCheckResult.Unhealthy("MQTT subscriptions unavailable");
        try {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Probe the columns actually required by the deployed application, without mutations.
            await db.QrAccesses.AsNoTracking().Select(q => new { q.DeviceRevision, q.DevicePhase, q.RfidExpectedJson }).Take(1).ToListAsync(token);
            await db.ReturnInspections.AsNoTracking().Take(1).ToListAsync(token);
            await db.EquipmentRfidTags.AsNoTracking().Take(1).ToListAsync(token);
            await db.CabinetHealths.AsNoTracking().Take(1).ToListAsync(token);
            await db.OperationSamples.AsNoTracking().Take(1).ToListAsync(token);
            await db.IotSensors.AsNoTracking().Take(1).ToListAsync(token);
            await db.IotNodes.AsNoTracking().Take(1).ToListAsync(token);
            await db.OperationAlerts.AsNoTracking().Take(1).ToListAsync(token);
            return HealthCheckResult.Healthy();
        } catch (Exception) when (!token.IsCancellationRequested) {
            return HealthCheckResult.Unhealthy("Database or required schema unavailable");
        }
    }
}
