using ClassHub_API.Data;
using ClassHub_API.Models;
using Microsoft.EntityFrameworkCore;
namespace ClassHub_API.Services;

// Room IDs are historical database identities; corridor MQTT IDs use the short alias
// already recorded in cabinet.mqtt_topic. No command may silently fall back to HALL-... .
public static class CabinetAddress {
    const string Prefix = "classhub/cabinet/";
    public static string? DeviceId(ThietBiIot cabinet) {
        var room = cabinet.ma_phong;
        if (string.IsNullOrWhiteSpace(room)) return null;
        if (!room.StartsWith("HALL-", StringComparison.Ordinal)) return ValidLegacy(room) ? room : null;
        if (!cabinet.mqtt_topic.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var alias = cabinet.mqtt_topic[Prefix.Length..];
        return Valid(alias) ? alias : null;
    }
    public static bool Valid(string? value) => value is { Length: >= 3 and <= 20 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    // Preserve existing room-cabinet routing, including old STANDARD IDs, until those
    // devices are migrated separately. The new firmware itself still accepts <=20.
    public static bool ValidLegacy(string? value) => value is { Length: >= 3 and <= 50 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static async Task<string?> RoomForDevice(AppDbContext db, string device) {
        if (!ValidLegacy(device)) return null;
        var topic=Prefix+device;
        var candidates=await db.thiet_bi_iot.AsNoTracking().Where(c=>c.ma_phong==device || c.mqtt_topic==topic)
            .Select(c=>new {c.ma_phong,c.mqtt_topic}).ToListAsync();
        var rooms=candidates.Where(c=>c.ma_phong!=null &&
            (c.ma_phong.StartsWith("HALL-",StringComparison.Ordinal) ? c.mqtt_topic==topic : c.ma_phong==device))
            .Select(c=>c.ma_phong!).Distinct(StringComparer.Ordinal).ToArray();
        return rooms.Length==1 ? rooms[0] : null;
    }
    public static async Task<string> ForRoom(AppDbContext db, string room) {
        var cabinet=await db.thiet_bi_iot.AsNoTracking().FirstOrDefaultAsync(c=>c.ma_phong==room);
        var device=cabinet==null?null:DeviceId(cabinet);
        if (device==null || await RoomForDevice(db,device)!=room)
            throw new InvalidOperationException("Cabinet MQTT ID is invalid or ambiguous.");
        return device;
    }
}
