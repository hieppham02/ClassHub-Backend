using ClassHub_API.Data;
using ClassHub_API.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ClassHub_API.Services;

public record RfidExpectedItem(int SnapshotId, int? EquipmentId, string Name, int Quantity, string[] Uids);
public record RfidItemStatus(int SnapshotId, int? EquipmentId, string Name, int Quantity,
    int Assigned, int Scanned, string[] Missing, string[] Observed);
public record RfidSummary(string Status, IReadOnlyList<RfidItemStatus> Items, string[] Unexpected);

public static class RfidSession
{
    public static bool ValidUid(string? uid) => uid != null && (uid.Length is 8 or 14 or 20) &&
        uid.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'));

    public static string[]? ParseObserved(string? json)
    {
        if (json == null) return null;
        try { return JsonSerializer.Deserialize<string[]>(json); }
        catch (JsonException) { return null; }
    }

    public static RfidSummary Summarize(string? expectedJson, string? observedJson)
    {
        var observed = ParseObserved(observedJson);
        List<RfidExpectedItem>? expected;
        try { expected = expectedJson == null ? null : JsonSerializer.Deserialize<List<RfidExpectedItem>>(expectedJson); }
        catch (JsonException) { expected = null; }
        if (expected == null || expected.Any(x => x.Uids == null || x.Quantity < 1))
            return new("UNCONFIGURED", [], observed ?? []);
        var seen = new HashSet<string>(observed ?? [], StringComparer.Ordinal);
        var all = new HashSet<string>(expected.SelectMany(e => e.Uids), StringComparer.Ordinal);
        var items = expected.Select(e => new RfidItemStatus(e.SnapshotId, e.EquipmentId, e.Name, e.Quantity,
            e.Uids.Length, e.Uids.Count(seen.Contains), e.Uids.Where(uid => !seen.Contains(uid)).ToArray(),
            e.Uids.Where(seen.Contains).ToArray())).ToList();
        var unexpected = seen.Where(uid => !all.Contains(uid)).Order().ToArray();
        var configured = items.Count > 0 && items.All(i => i.Assigned == i.Quantity) && all.Count == items.Sum(i => i.Assigned);
        var status = !configured ? "UNCONFIGURED" : observed == null ? "NO_DATA" : unexpected.Length > 0 ? "WRONG" :
            items.Any(i => i.Scanned != i.Quantity) ? "INCOMPLETE" : "MATCH";
        return new(status, items, unexpected);
    }

    public static async Task<string> Snapshot(AppDbContext db, int sessionId)
    {
        var equipment = await db.phieu_muon_thiet_bi.AsNoTracking().Where(x => x.phieu_muon_id == sessionId)
            .OrderBy(x => x.id).Select(x => new { x.id, x.thiet_bi_id, x.ten_thiet_bi, x.so_luong }).ToListAsync();
        var ids = equipment.Where(e => e.thiet_bi_id != null).Select(e => e.thiet_bi_id!.Value).Distinct().ToArray();
        var tags = await db.EquipmentRfidTags.AsNoTracking().Where(t => ids.Contains(t.EquipmentId)).OrderBy(t => t.Uid).ToListAsync();
        var list = equipment.Select(e => new RfidExpectedItem(e.id, e.thiet_bi_id, e.ten_thiet_bi, e.so_luong,
            tags.Where(t => t.EquipmentId == e.thiet_bi_id).Select(t => t.Uid).ToArray())).ToList();
        if (list.Sum(x => x.Uids.Length) > 32) throw new QrFlowException(409, "Tủ vượt giới hạn 32 UID RFID của firmware.");
        return JsonSerializer.Serialize(list);
    }
}
