using ClassHub_API.Data;
using ClassHub_API.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ClassHub_API.Services;
public class CabinetHealthPayload
{
    public int SchemaVersion { get; set; }
    public string? Source { get; set; }
    public string? BootId { get; set; }
    public long BootSequence { get; set; }
    public long UptimeMs { get; set; }
    public long Sequence { get; set; }
    public string? FirmwareId { get; set; }
    public string? FirmwareVersion { get; set; }
    public bool ClockSynced { get; set; }
    public DateTime? MeasuredAtUtc { get; set; }
    public int? Rssi { get; set; }
    public long? HeapFree { get; set; }
    public long? HeapLargest { get; set; }
    public long? PsramFree { get; set; }
    public bool? ReaderInitialized { get; set; }
    public long? LastRfidScanAgeMs { get; set; }
    public string? LastRfidUid { get; set; }
    public long? RfidScanSequence { get; set; }
    public bool? CameraRecentFrame { get; set; }
    public long? CameraFrameAgeMs { get; set; }
    public int? CameraFps100 { get; set; }
    public bool? DoorOpen { get; set; }
    public bool? SecurityProvisioned { get; set; }
    public bool? ApOpen { get; set; }
    public bool? OtaActive { get; set; }
    public string? CurrentError { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAtUtc { get; set; }
}
public record CabinetHealthView(DateTime ReceivedAtUtc, double AgeSeconds, bool Stale, string Quality, CabinetHealthPayload Metrics);

public static class CabinetHealthService
{
    public static bool Valid(CabinetHealthPayload p) => p.SchemaVersion == 1 && p.Source == "REAL" &&
        p.BootSequence is > 0 and <= uint.MaxValue && p.Sequence is > 0 and <= uint.MaxValue && p.UptimeMs >= 0 && p.UptimeMs <= 9007199254740991 &&
        p.BootId?.Length == 32 && p.BootId.All(Uri.IsHexDigit) &&
        p.FirmwareId?.Length is > 0 and <= 80 && p.FirmwareVersion?.Length is > 0 and <= 80 &&
        (p.Rssi == null || p.Rssi is >= -127 and <= 0) &&
        (p.HeapFree == null || p.HeapFree is >= 0 and <= 16777216) &&
        (p.HeapLargest == null || p.HeapLargest is >= 0 and <= 16777216) &&
        (p.PsramFree == null || p.PsramFree is >= 0 and <= 16777216) &&
        (p.CameraFps100 == null || p.CameraFps100 is >= 0 and <= 100000) &&
        (p.CameraFrameAgeMs == null || p.CameraFrameAgeMs is >= -1 and <= 9007199254740991) &&
        (p.LastRfidScanAgeMs == null || p.LastRfidScanAgeMs is >= -1 and <= 9007199254740991) &&
        ((p.LastRfidUid == null && (p.RfidScanSequence == null || p.RfidScanSequence == 0)) ||
            RfidSession.ValidUid(p.LastRfidUid) && p.RfidScanSequence is > 0 and <= uint.MaxValue) &&
        (p.LastError?.Length ?? 0) <= 80 && (p.CurrentError?.Length ?? 0) <= 80;

    public static CabinetHealthView? View(CabinetHealth? row, DateTime now)
    {
        if (row == null) return null;
        CabinetHealthPayload? payload;
        try { payload = JsonSerializer.Deserialize<CabinetHealthPayload>(row.PayloadJson); }
        catch (JsonException) { return null; }
        if (payload == null) return null;
        var received = DateTime.SpecifyKind(row.ReceivedAtUtc, DateTimeKind.Utc);
        var age = Math.Max(0, (now - received).TotalSeconds);
        return new(received, Math.Round(age, 1), age > 45, payload.ClockSynced ? "CLOCK_SYNCED" : "UNSYNCED", payload);
    }

    // Caller owns SaveChanges/transaction and must have verified that the room has a cabinet.
    public static async Task<bool> Accept(AppDbContext db, string room, CabinetHealthPayload payload, DateTime now)
    {
        if (!Valid(payload)) return false;
        var row = await db.CabinetHealths.FindAsync(room);
        if (row != null && (payload.BootSequence < row.BootSequence || payload.BootSequence == row.BootSequence &&
            (payload.BootId != row.BootId || payload.UptimeMs < row.UptimeMs || payload.Sequence <= row.MessageSequence))) return false;
        if (!payload.ClockSynced || payload.MeasuredAtUtc?.Kind != DateTimeKind.Utc ||
            payload.MeasuredAtUtc < now.AddMinutes(-5) || payload.MeasuredAtUtc > now.AddMinutes(1))
        { payload.ClockSynced = false; payload.MeasuredAtUtc = null; }
        if (payload.LastErrorAtUtc?.Kind != DateTimeKind.Utc || payload.LastErrorAtUtc > now.AddMinutes(1)) payload.LastErrorAtUtc = null;
        if (row == null) { row = new CabinetHealth { RoomId = room }; db.CabinetHealths.Add(row); }
        row.ReceivedAtUtc = now; row.BootSequence = payload.BootSequence; row.BootId = payload.BootId!; row.UptimeMs = payload.UptimeMs;
        row.MessageSequence = payload.Sequence;
        row.PayloadJson = JsonSerializer.Serialize(payload);
        var previous = await db.OperationSamples.AsNoTracking().Where(s => s.RoomId == room)
            .OrderByDescending(s => s.Id).FirstOrDefaultAsync();
        DateTime? since = payload.DoorOpen == true ? now : null;
        if (payload.DoorOpen == true && previous?.DoorOpen == true && previous.BootSequence == payload.BootSequence
            && now >= previous.ReceivedAtUtc && (now - previous.ReceivedAtUtc).TotalSeconds <= 45) since = previous.DoorOpenSinceUtc ?? now;
        db.OperationSamples.Add(new OperationSample { RoomId=room, ReceivedAtUtc=now, BootSequence=payload.BootSequence,
            MessageSequence=payload.Sequence, DoorOpen=payload.DoorOpen, DoorOpenSinceUtc=since, PayloadJson=row.PayloadJson });
        return true;
    }
}
