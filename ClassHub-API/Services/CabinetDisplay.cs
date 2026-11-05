using ClassHub_API.Data;
using ClassHub_API.Models;
using Microsoft.EntityFrameworkCore;
namespace ClassHub_API.Services;

// Presentation defaults never write a physical measurement or change allocation/command guards.
public static class CabinetDisplay {
    public static string Status(ThietBiIot cabinet,string? roomStatus,PhieuMuon? booking,bool blocked=false) {
        if(blocked || roomStatus is "BAO_TRI" or "TAM_KHOA" || cabinet.trang_thai_khoa=="ERROR") return "Bảo trì";
        if(booking?.trang_thai=="RETURNING")return "Đang xác nhận trả";
        return booking==null?"Trống":"Đang mượn";
    }
    public static bool DoorOpen(bool? current,bool? previous=null)=>current ?? previous ?? false;
    public static string DoorLabel(bool? current,bool? previous=null)=>DoorOpen(current,previous)?"Mở":"Đóng";
    public static async Task<Dictionary<string,bool>> LastDoors(AppDbContext db,string[] rooms) {
        if(rooms.Length==0)return [];
        var known=db.OperationSamples.AsNoTracking().Where(s=>rooms.Contains(s.RoomId) && s.DoorOpen.HasValue);
        return await known.Where(s=>s.Id==known.Where(t=>t.RoomId==s.RoomId).OrderByDescending(t=>t.Id).Select(t=>t.Id).First())
            .Select(s=>new{s.RoomId,Open=s.DoorOpen!.Value}).ToDictionaryAsync(s=>s.RoomId,s=>s.Open);
    }
}
