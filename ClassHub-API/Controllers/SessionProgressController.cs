using ClassHub_API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ClassHub_API.Controllers;

[ApiController, Authorize, Route("api/operations/sessions/{id:int}/progress")]
public class SessionProgressController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var user = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var admin = User.IsInRole("ADMIN");
        var session = await db.phieu_muon.AsNoTracking().FirstOrDefaultAsync(s => s.id == id &&
            (admin || s.ma_sv == user || s.ma_sv_uy_quyen == user), ct);
        if (session == null || string.IsNullOrEmpty(user)) return NotFound();
        var qr = await db.QrAccesses.AsNoTracking().OrderByDescending(q => q.Id).FirstOrDefaultAsync(q => q.SessionId == id, ct);
        var cabinet = await db.thiet_bi_iot.AsNoTracking().FirstOrDefaultAsync(c => c.ma_phong == session.ma_phong, ct);
        var physicalOpened = qr?.DevicePhase is "OPENED" or "CLOSED" || qr?.State == "CLOSED";
        var closed = qr?.State == "CLOSED";
        var complete = qr?.Purpose == "RETURN" ? session.trang_thai == "COMPLETED" : closed && session.trang_thai is "IN_USE" or "ACTIVE" or "RETURNING" or "COMPLETED";
        Response.Headers.CacheControl = "no-store";
        return Ok(new { sessionId = id, status = session.trang_thai, phase = session.QrPhase,
            purpose = qr?.Purpose, requestRecorded = qr?.UsedAt != null,
            requestedAtUtc = qr?.UsedAt == null ? (DateTime?)null : DateTime.SpecifyKind(qr.UsedAt.Value, DateTimeKind.Utc),
            deviceAcknowledged = qr?.DeviceRevision > 0 || qr?.State is "OPENED" or "CLOSED" or "FAILED",
            relayTriggered = qr?.DevicePhase is "UNLOCKED" or "OPENED" or "CLOSED" || qr?.State is "OPENED" or "CLOSED",
            doorOpened = physicalOpened, doorClosed = closed,
            needsReview = qr?.Purpose == "RETURN" && closed && !complete,
            completed = complete, failed = qr?.State == "FAILED" || session.QrPhase == "RETURN_FAILED",
            devicePhase = qr?.DevicePhase, revision = qr?.DeviceRevision,
            legacy = qr?.UsedAt != null && qr.DeviceRevision == 0,
            isOnline = cabinet?.trang_thai_mang == true,
            canRetrySameCommand = qr?.State == "USED" && cabinet?.trang_thai_mang == true,
            commandId = qr?.CommandId });
    }
}
