using ClassHub_API.Data;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace ClassHub_API.Controllers;

[ApiController, Authorize(Roles = "ADMIN"), Route("api/admin/cabinet-health")]
public class CabinetHealthController(AppDbContext db) : ControllerBase
{
    [HttpGet("{room}")]
    public async Task<IActionResult> Get(string room, CancellationToken ct)
    {
        if (!await db.thiet_bi_iot.AnyAsync(c => c.ma_phong == room, ct)) return NotFound();
        var row = await db.CabinetHealths.AsNoTracking().FirstOrDefaultAsync(h => h.RoomId == room, ct);
        Response.Headers.CacheControl = "no-store";
        return Ok(new { health = CabinetHealthService.View(row, DateTime.UtcNow) });
    }
}
