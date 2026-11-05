using ClassHub_API.Models;
using ClassHub_API.Services;
using ClassHub_API.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
int checks=0;
void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);checks++;Console.WriteLine("PASS: "+name);}
var cabinet=new ThietBiIot {trang_thai_mang=false,trang_thai_khoa="LOCKED"};
Check(CabinetDisplay.Status(cabinet,"HOAT_DONG",null)=="Trống","Offline connection does not replace stored availability");
var session=new PhieuMuon {trang_thai="ACTIVE"};
Check(CabinetDisplay.Status(cabinet,"HOAT_DONG",session)=="Đang mượn","Offline borrowing remains borrowed");
session.trang_thai="RETURNING";
Check(CabinetDisplay.Status(cabinet,"HOAT_DONG",session)=="Đang xác nhận trả","Offline return keeps return-review state");
Check(CabinetDisplay.Status(cabinet,"BAO_TRI",session)=="Bảo trì","Explicit room maintenance remains maintenance");
Check(CabinetDisplay.Status(cabinet,"HOAT_DONG",null,true)=="Bảo trì","Allocation blocking remains visible");
cabinet.trang_thai_khoa="ERROR";
Check(CabinetDisplay.Status(cabinet,"HOAT_DONG",null)=="Bảo trì","Recorded cabinet errors are not hidden");
cabinet.trang_thai_khoa="UNLOCKED";
Check(CabinetDisplay.DoorLabel(null)=="Đóng","No physical door measurement defaults to closed, not latch state");
Check(CabinetDisplay.DoorLabel(null,true)=="Mở","Last known open door remains open");
Check(CabinetDisplay.DoorLabel(null,false)=="Đóng","Last known closed door remains closed");
Check(CabinetDisplay.DoorLabel(false,true)=="Đóng","New physical closure overrides older open measurement");
Check(CabinetDisplay.DoorLabel(true,false)=="Mở","New physical opening overrides older closed measurement");
var old=new CabinetHealth {ReceivedAtUtc=DateTime.UtcNow.AddDays(-1),PayloadJson=JsonSerializer.Serialize(new CabinetHealthPayload {DoorOpen=true})};
var health=CabinetHealthService.View(old,DateTime.UtcNow);
Check(health?.Stale==true && CabinetDisplay.DoorLabel(health.Metrics.DoorOpen)=="Mở","Stale telemetry retains actual last physical state");
var connection=Environment.GetEnvironmentVariable("CLASSHUB_DISPLAY_READONLY_CONNECTION");
if(connection!=null){
 using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySql(connection,new MariaDbServerVersion(new Version(10,4,32))).Options);
 Check((await CabinetDisplay.LastDoors(db,[])).Count==0,"No cabinets needs no SQL query");
 Check((await CabinetDisplay.LastDoors(db,["__no_such_room__"])).Count==0,"Latest-known-door SQL translates and executes with no data");
}
Console.WriteLine("PASS: "+checks+" cabinet presentation checks");
