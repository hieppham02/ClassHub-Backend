using ClassHub_API.Data;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
namespace ClassHub_API.Controllers;
[ApiController,Authorize(Roles="ADMIN"),Route("api/admin/operations-telemetry")]
public class OperationTelemetryController(AppDbContext db):ControllerBase {
 [HttpGet("cabinets/{room}/history")]
 public async Task<IActionResult> History(string room,[FromQuery]int hours=24,[FromQuery]long? before=null) {
    if(hours<1||hours>720||before<=0)return BadRequest();
    var cutoff=DateTime.UtcNow.AddHours(-hours);
    var rows=await db.OperationSamples.AsNoTracking().Where(s=>s.RoomId==room && s.ReceivedAtUtc>=cutoff && (!before.HasValue||s.Id<before))
        .OrderByDescending(s=>s.Id).Take(201).ToListAsync();
    var page=rows.Take(200).ToList();
    return Ok(new{source="REAL",timeBasis="SERVER_RECEIVED_UTC",items=page.Select(s=>new{s.Id,s.ReceivedAtUtc,s.DoorOpen,s.DoorOpenSinceUtc,
        metrics=JsonSerializer.Deserialize<CabinetHealthPayload>(s.PayloadJson)}),nextBefore=rows.Count>200?(long?)page.Last().Id:null});
 }
 [HttpGet("alerts")]
 public async Task<IActionResult> Alerts([FromQuery]string? room=null,[FromQuery]long? before=null,[FromQuery]string? status=null) {
    if(before<=0 || status!=null && status!="OPEN" && status!="RESOLVED")return BadRequest();
    var rows=await db.OperationAlerts.AsNoTracking().Where(a=>(room==null||a.RoomId==room)&&(!before.HasValue||a.Id<before)&&(status==null||a.Status==status))
        .OrderByDescending(a=>a.Id).Take(101).ToListAsync();
    return Ok(new{source="REAL",items=rows.Take(100).Select(a=>new{a.Id,a.RoomId,a.Rule,a.Status,a.StartedAtUtc,a.ResolvedAtUtc,a.LastEvaluatedAtUtc,
        evidence=JsonSerializer.Deserialize<JsonElement>(a.EvidenceJson)}),nextBefore=rows.Count>100?(long?)rows[99].Id:null});
 }
}
