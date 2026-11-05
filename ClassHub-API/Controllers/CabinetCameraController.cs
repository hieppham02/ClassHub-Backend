using ClassHub_API.Data;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Buffers.Binary;
using System.Net.WebSockets;

namespace ClassHub_API.Controllers;
[ApiController, Authorize(Roles="ADMIN"), Route("api/admin/cabinets/{cabinetId:int}/camera")]
public class CabinetCameraController(AppDbContext db,CabinetCamera camera) : ControllerBase {
    [HttpGet("stream")]
    public async Task Stream(int cabinetId,CancellationToken ct) {
        var cabinet=await db.thiet_bi_iot.AsNoTracking().FirstOrDefaultAsync(c=>c.id==cabinetId,ct);
        if(cabinet?.ma_phong==null){Response.StatusCode=404;return;}
        if(!camera.Configured(cabinet.ma_phong)) {
            Response.StatusCode=503;await Response.WriteAsJsonAsync(new {message="Camera chưa được kết nối với máy chủ."},ct);return;
        }
        Response.Headers.CacheControl="no-store";
        Response.Headers["X-Accel-Buffering"]="no";
        try {
            await foreach(var photo in camera.Stream(cabinet.ma_phong,ct)) {
                Response.ContentType="application/x-classhub-jpeg-stream";
                var length=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(length,photo.Jpeg.Length);
                await Response.Body.WriteAsync(length,ct);await Response.Body.WriteAsync(photo.Jpeg,ct);
                await Response.Body.FlushAsync(ct);
            }
        } catch(Exception ex) when(ex is InvalidDataException or WebSocketException or OperationCanceledException or HttpRequestException or IOException) {
            if(ct.IsCancellationRequested)return;
            if(Response.HasStarted){HttpContext.Abort();return;}
            Response.StatusCode=503;Response.ContentType="application/json";
            await Response.WriteAsJsonAsync(new {message=ex is InvalidDataException?ex.Message:"Không kết nối được camera. Hãy kiểm tra kết nối của tủ."},ct);
        }
    }
}
