using ClassHub_API.Data;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace ClassHub_API.Controllers;

[ApiController, Authorize, Route("api/operations/sessions/{id:int}/inspection")]
public class ReturnInspectionController(AppDbContext db, ReturnInspectionService inspections) : ControllerBase
{
    private async Task<bool> Allowed(int id, CancellationToken ct)
    {
        var user = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var admin = User.IsInRole("ADMIN");
        return !string.IsNullOrEmpty(user) && await db.phieu_muon.AnyAsync(s => s.id == id &&
            (admin || s.ma_sv == user || s.ma_sv_uy_quyen == user), ct);
    }

    [HttpGet]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        if (!await Allowed(id, ct)) return NotFound();
        var qr = await db.QrAccesses.AsNoTracking().OrderByDescending(q => q.Id).FirstOrDefaultAsync(q => q.SessionId == id, ct);
        Response.Headers.CacheControl = "no-store";
        var command = qr?.CommandId;
        var latest = await db.ReturnInspections.AsNoTracking().Where(i => i.SessionId == id && i.CommandId == command)
            .OrderByDescending(i => i.Id).FirstOrDefaultAsync(ct);
        var expected = await inspections.Expected(id, ct);
        var session = await db.phieu_muon.AsNoTracking().SingleAsync(s => s.id == id, ct);
        return Ok(new { expected, commandId = qr?.CommandId, devicePhase = qr?.DevicePhase, deviceRevision = qr?.DeviceRevision,
            canCapture = session.trang_thai == "RETURNING" && qr?.Purpose == "RETURN" && qr.State == "CLOSED" && session.is_cabinet_open != true,
            rfidPassed = qr?.RfidPassed, rfid = RfidSession.Summarize(qr?.RfidExpectedJson, qr?.RfidObservedJson), cameraPassed = qr?.CameraPassed,
            inspection = latest == null ? null : new { latest.Id, latest.Status, latest.Reason, latest.CapturedAtUtc,
                latest.ImageSha256, latest.CameraSequence, latest.ReviewedBy, latest.ReviewedAtUtc,
                imageDataUrl = latest.ImageJpeg == null ? null : "data:image/jpeg;base64," + Convert.ToBase64String(latest.ImageJpeg),
                analysis = latest.AnalysisJson == null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(latest.AnalysisJson) } });
    }

    [HttpPost("capture")]
    public async Task<IActionResult> Capture(int id, CancellationToken ct)
    {
        if (!await Allowed(id, ct)) return NotFound();
        try { var inspection = await inspections.Capture(id, true, ct); return Ok(new { inspection.Id, inspection.Status, inspection.Reason }); }
        catch (QrFlowException ex) { return StatusCode(ex.Status, new { message = ex.Message }); }
    }
}
