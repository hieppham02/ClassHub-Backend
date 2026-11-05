using ClassHub_API.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ClassHub_API.Services;
public record SelectableRfidItem(int EquipmentId,string Name,string Uid,string Condition);
public static class RfidSelectionRules {
    public static bool Valid(IReadOnlyList<string>? selected,IReadOnlyList<SelectableRfidItem> available) =>
        selected is {Count:>0 and <=32} && selected.All(RfidSession.ValidUid) &&
        selected.Distinct(StringComparer.Ordinal).Count()==selected.Count &&
        selected.All(uid=>available.Count(a=>a.Uid==uid)==1);
    public static bool Received(string? expected,string? observed) => RfidSession.Summarize(expected,observed).Status=="MATCH";
}
public partial class QrAccessService {
    private async Task<List<SelectableRfidItem>> Available(PhieuMuon session) {
        return await (from t in db.EquipmentRfidTags.AsNoTracking() join e in db.thiet_bi.AsNoTracking() on t.EquipmentId equals e.id
            where e.ma_phong==session.ma_phong && e.UsageScope=="CABINET" && e.tinh_trang=="TOT" && e.so_luong>0 &&
                e.Catalog.Enabled && db.EquipmentRfidTags.Count(x=>x.EquipmentId==e.id)<=e.so_luong
            orderby e.ten_thiet_bi,t.Uid select new SelectableRfidItem(e.id,e.ten_thiet_bi,t.Uid,e.tinh_trang)).ToListAsync();
    }
    public async Task<object> EquipmentOptions(int id,string user) {
        var session=await db.phieu_muon.AsNoTracking().FirstOrDefaultAsync(s=>s.id==id);
        if(session==null)throw new QrFlowException(404,"Không tìm thấy phiếu.");
        await Authorize(session,user);
        var latest=await db.QrAccesses.AsNoTracking().OrderByDescending(q=>q.Id).FirstOrDefaultAsync(q=>q.SessionId==id);
        List<RfidExpectedItem>? selected=null;
        if(latest?.RfidExpectedJson!=null) {
            try {selected=JsonSerializer.Deserialize<List<RfidExpectedItem>>(latest.RfidExpectedJson);}
            catch(JsonException){}
        }
        return new {items=await Available(session),selectedUids=selected?.SelectMany(e=>e.Uids).ToArray()??[],
            selectionIssued=latest?.State=="ISSUED" && latest.Purpose=="RECEIVE" && selected?.Count>0 && latest.ExpiresAt>DateTime.UtcNow};
    }
    private async Task<string> SelectEquipment(PhieuMuon session,string[]? selectedUids) {
        var available=await Available(session);
        if(!RfidSelectionRules.Valid(selectedUids,available))
            throw new QrFlowException(409,"Chọn ít nhất 1 món có RFID hợp lệ trong tủ, tối đa 32 món. Hãy tải lại nếu danh sách thay đổi.");
        var selected=available.Where(e=>selectedUids!.Contains(e.Uid,StringComparer.Ordinal)).ToList();
        var old=await db.phieu_muon_thiet_bi.Where(e=>e.phieu_muon_id==session.id).ToListAsync();
        var groups=selected.GroupBy(e=>e.EquipmentId).ToList();
        db.phieu_muon_thiet_bi.RemoveRange(old.Where(e=>!groups.Any(g=>g.Key==e.thiet_bi_id)));
        var snapshots=new List<PhieuMuonThietBi>();
        foreach(var group in groups) {
            var row=old.FirstOrDefault(e=>e.thiet_bi_id==group.Key);
            if(row==null){row=new PhieuMuonThietBi{phieu_muon_id=session.id,thiet_bi_id=group.Key};db.phieu_muon_thiet_bi.Add(row);}
            row.ten_thiet_bi=group.First().Name;row.so_luong=group.Count();row.tinh_trang_du_kien=group.First().Condition;
            snapshots.Add(row);
        }
        await db.SaveChangesAsync();
        return JsonSerializer.Serialize(snapshots.OrderBy(e=>e.id).Select(e=>new RfidExpectedItem(e.id,e.thiet_bi_id,e.ten_thiet_bi,e.so_luong,
            selected.Where(t=>t.EquipmentId==e.thiet_bi_id).Select(t=>t.Uid).Order().ToArray())));
    }
}
