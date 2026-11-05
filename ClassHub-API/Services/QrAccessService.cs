using ClassHub_API.Data;
using ClassHub_API.Hubs;
using ClassHub_API.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassHub_API.Services;

public class QrFlowException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public record QrDeviceResult(int SessionId, string CommandId, string Event, bool? RfidPassed = null,
    bool? CameraPassed = null, long Sequence = 0, string[]? RfidUids = null, int RfidSequence = 0,
    long Revision = 0, string? Phase = null, bool? DoorOpened = null);

public partial class QrAccessService(AppDbContext db, IMqttService mqtt, IHubContext<CabinetHub> hub)
{
    // Return verification is mandatory; AI/device results are evidence, never a substitute for review.
    public static readonly bool RequireReturnVerification = true;
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static void Fail(int status, string message) => throw new QrFlowException(status, message);
    private async Task<PhieuMuon> LockSession(int id)
    {
        var session = (await db.phieu_muon.FromSqlInterpolated($"SELECT * FROM borrowing_session WHERE id={id} FOR UPDATE").ToListAsync()).FirstOrDefault();
        if (session == null) Fail(404, "Không tìm thấy phiếu mượn.");
        return session!;
    }
    private async Task Authorize(PhieuMuon session, string userId)
    {
        if (session.ma_sv != userId && session.ma_sv_uy_quyen != userId) Fail(403, "Bạn không có quyền thao tác phiếu này.");
        if (!await db.tai_khoan.AnyAsync(a => a.ma_sv == userId && a.trang_thai)) Fail(403, "Tài khoản không hoạt động.");
        if (!session.QrEnabled) Fail(409, "Phiếu chưa được kích hoạt QR.");
        if (!await db.phong_hoc.AnyAsync(r => r.ma_phong == session.ma_phong && (r.LocationType == "ROOM" || r.LocationType == "CORRIDOR") && r.trang_thai == "HOAT_DONG")) Fail(409, "Tủ không còn hoạt động.");
    }
    private async Task<ThietBiIot> Cabinet(PhieuMuon session, bool returning = false)
    {
        var cabinet = (await db.thiet_bi_iot.FromSqlInterpolated($"SELECT * FROM cabinet WHERE room_id={session.ma_phong} FOR UPDATE").ToListAsync()).FirstOrDefault();
        if (cabinet == null) Fail(404, "Tủ chưa được cấu hình.");
        if (!cabinet!.trang_thai_mang) Fail(503, "Tủ đang ngoại tuyến.");
        if (!returning && await AllocationPolicy.IsBlocked(db, session.ma_phong)) Fail(409, "Tủ đang bị chặn cấp phát do sự cố chưa được gỡ chặn.");
        if (cabinet.trang_thai_khoa == "ERROR") Fail(409, "Tủ đang gặp lỗi hoặc bảo trì.");
        if (await db.phieu_muon.AnyAsync(s => s.id != session.id && s.ma_phong == session.ma_phong &&
            (s.trang_thai == "ACTIVE" || s.trang_thai == "IN_USE" || s.trang_thai == "RETURNING" || s.QrPhase == "RECEIVE_OPEN_REQUESTED" || s.QrPhase == "RECEIVE_OPEN")))
            Fail(409, "Tủ đang có lượt mượn chưa trả. Chờ lượt trước hoàn tất.");
        return cabinet;
    }
    private void Log(PhieuMuon session, string action, string? userId, object detail) =>
        db.nhat_ky_he_thong.Add(new NhatKyHeThong { ma_sv = userId, hanh_dong = action,
            chi_tiet = JsonSerializer.Serialize(new { sessionId = session.id, roomId = session.ma_phong, detail }),
            thoi_gian = DateTime.Now, user_agent = "ClassHub QR" });

    public async Task<object> Issue(int id, string userId, string purpose)
    {
        if (purpose is not ("RECEIVE" or "RETURN")) Fail(400, "Thao tác QR không hợp lệ.");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        var session = await LockSession(id); await Authorize(session, userId);
        var cabinet = await Cabinet(session, purpose == "RETURN");
        var deviceId = await CabinetAddress.ForRoom(db, session.ma_phong);
        if (cabinet.trang_thai_khoa != "LOCKED" || session.is_cabinet_open == true) Fail(409, "Đóng cửa tủ trước khi cấp QR.");
        if (purpose == "RECEIVE" && session.trang_thai != "PENDING") Fail(409, "Phiếu không còn ở bước nhận đồ.");
        if (purpose == "RETURN" && session.trang_thai is not ("IN_USE" or "ACTIVE" or "RETURNING")) Fail(409, "Chỉ trả phiếu đã nhận thiết bị.");
        var previous = await db.QrAccesses.Where(q => q.SessionId == id && q.State != "REVOKED").ToListAsync();
        if (previous.Any(q => q.State == "USED" || q.State == "OPENED")) Fail(409, "Đang chờ tủ phản hồi lệnh trước. Không cấp mã mới.");
        foreach (var q in previous) q.State = "REVOKED";
        if (purpose == "RECEIVE" && !await db.phieu_muon_thiet_bi.AnyAsync(e => e.phieu_muon_id == id))
        {
            var equipment = await db.thiet_bi.Where(e => e.ma_phong == session.ma_phong && e.UsageScope == "CABINET" && e.so_luong > 0).ToListAsync();
            foreach (var e in equipment) db.phieu_muon_thiet_bi.Add(new PhieuMuonThietBi { phieu_muon_id = id, thiet_bi_id = e.id, ten_thiet_bi = e.ten_thiet_bi, so_luong = e.so_luong, tinh_trang_du_kien = e.tinh_trang });
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var qr = new QrAccess { SessionId = id, TokenHash = Hash(token), Purpose = purpose, ExpiresAt = DateTime.UtcNow.AddMinutes(3) };
        db.QrAccesses.Add(qr);
        if (purpose == "RETURN") { session.trang_thai = "RETURNING"; session.ma_sv_tra = userId; }
        session.QrPhase = purpose + "_QR";
        Log(session, "QR_ISSUED", userId, new { purpose, qr.ExpiresAt });
        await db.SaveChangesAsync(); await tx.CommitAsync();
        try {
            await mqtt.PublishAsync($"backend/cabinet/{deviceId}/qr", JsonSerializer.Serialize(new {
                sessionId = id, room = deviceId, purpose, qrId = qr.Id,
                content = "classhub:qr:v1:" + token, expiresAt = qr.ExpiresAt, action = "display_qr" }));
        } catch {
            qr.State = "REVOKED"; session.QrPhase = purpose + "_QR_FAILED"; await db.SaveChangesAsync();
            Fail(503, "Chưa gửi được QR tới màn hình tủ. Hãy cấp lại QR.");
        }
        return new { qrId = qr.Id, purpose, expiresAt = qr.ExpiresAt, message = "QR đã gửi tới màn hình tủ. Hãy quét mã tại tủ." };
    }

    private async Task<QrAccess> Validate(PhieuMuon session, string token, string purpose)
    {
        if (token.Length != 64 || token.Any(c => !Uri.IsHexDigit(c))) Fail(400, "Mã QR không hợp lệ.");
        var hash = Hash(token);
        var qr = await db.QrAccesses.FirstOrDefaultAsync(q => q.SessionId == session.id && q.TokenHash == hash && q.Purpose == purpose);
        if (qr == null || qr.State != "ISSUED") Fail(409, "QR sai phiếu, sai thao tác, đã sử dụng hoặc đã bị thay thế.");
        if (qr!.ExpiresAt <= DateTime.UtcNow) Fail(410, "QR đã hết hạn. Hãy cấp lại QR.");
        if ((purpose == "RECEIVE" && session.trang_thai != "PENDING") || (purpose == "RETURN" && session.trang_thai != "RETURNING")) Fail(409, "Phiếu không còn ở bước tương ứng với QR.");
        if (purpose == "RECEIVE") {
            var slot = await db.cau_hinh_ca_hoc.FindAsync(session.ca_muon);
            if (slot == null) Fail(409, "Ca học không tồn tại.");
            // TEMP QR TEST: uncomment this block to restore the pickup time window.
            // var start = session.ngay_muon.Date.Add(slot!.gio_bat_dau).AddMinutes(-15);
            // var end = session.ngay_muon.Date.Add(slot.gio_ket_thuc);
            // if (DateTime.Now < start || DateTime.Now >= end) Fail(409, "Chỉ nhận đồ từ 15 phút trước ca đến hết ca đã đăng ký.");
        }
        return qr;
    }

    public async Task<object> Preview(int id, string userId, string token, string purpose)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        var session = await LockSession(id); await Authorize(session, userId);
        var qr = await Validate(session, token, purpose); await Cabinet(session, purpose == "RETURN");
        var deviceId = await CabinetAddress.ForRoom(db, session.ma_phong);
        var room = await db.phong_hoc.FindAsync(session.ma_phong);
        await tx.CommitAsync();
        try {
            await mqtt.PublishAsync($"backend/cabinet/{deviceId}/action", JsonSerializer.Serialize(new {
                room = deviceId, sessionId = id, action = "qr_scanned", purpose = qr.Purpose, expiresAt = DateTime.SpecifyKind(qr.ExpiresAt, DateTimeKind.Utc) }));
        } catch (Exception ex) {
            // Audible feedback is optional; a valid scan still permits confirmation.
            System.Diagnostics.Trace.TraceWarning("QR scan feedback delivery failed for session {0}: {1}", id, ex.Message);
        }
        return new { sessionId = id, roomId = session.ma_phong, roomName = room!.ten_phong, purpose = qr.Purpose, expiresAt = qr.ExpiresAt };
    }

    public async Task<object> Open(int id, string userId, string token, string purpose)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        var session = await LockSession(id); await Authorize(session, userId);
        var qr = await Validate(session, token, purpose); var cabinet = await Cabinet(session, purpose == "RETURN");
        var deviceId = await CabinetAddress.ForRoom(db, session.ma_phong);
        if (cabinet.trang_thai_khoa != "LOCKED" || session.is_cabinet_open == true) Fail(409, "Cửa tủ chưa đóng.");
        var receiveSnapshot = purpose == "RETURN" ? await db.QrAccesses.AsNoTracking()
            .Where(q => q.SessionId == id && q.Purpose == "RECEIVE" && q.RfidExpectedJson != null)
            .OrderByDescending(q => q.Id).Select(q => q.RfidExpectedJson).FirstOrDefaultAsync() : null;
        qr.RfidExpectedJson = receiveSnapshot ?? await RfidSession.Snapshot(db, id);
        qr.RfidObservedJson = null; qr.RfidSequence = 0; qr.RfidPassed = null;
        qr.State = "USED"; qr.UsedAt = DateTime.UtcNow; qr.CommandId = Guid.NewGuid().ToString("N");
        if (purpose == "RETURN") session.ma_sv_tra = userId;
        session.QrPhase = purpose + "_OPEN_REQUESTED";
        Log(session, "QR_OPEN_REQUESTED", userId, new { purpose, qr.CommandId });
        await db.SaveChangesAsync(); await tx.CommitAsync();
        var expected = await db.phieu_muon_thiet_bi.Where(e => e.phieu_muon_id == id).Select(e => new { equipmentId = e.thiet_bi_id, name = e.ten_thiet_bi, quantity = e.so_luong }).ToListAsync();
        try {
            await mqtt.PublishAsync($"backend/cabinet/{deviceId}/action", JsonSerializer.Serialize(new {
                id = id.ToString(), sessionId = id, room = deviceId, action = "open", purpose,
                commandId = qr.CommandId, expiresAt = DateTime.UtcNow.AddSeconds(30), expectedEquipment = expected }));
        } catch {
            // Delivery may have happened before a network failure. Keep the consumed QR and
            // command correlation; retrying uses the same command ID, never a second command.
            Fail(503, "Chưa xác nhận gửi lệnh mở. Kiểm tra trạng thái tủ hoặc gửi lại cùng lệnh.");
        }
        return new { commandId = qr.CommandId, message = "Đã gửi lệnh mở; chờ tủ xác nhận." };
    }

    public async Task<object> Retry(int id, string userId)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        var session = await LockSession(id); await Authorize(session, userId); await Cabinet(session, session.trang_thai == "RETURNING");
        var deviceId = await CabinetAddress.ForRoom(db, session.ma_phong);
        var qr = await db.QrAccesses.OrderByDescending(q => q.Id).FirstOrDefaultAsync(q => q.SessionId == id);
        if (qr == null || qr.State != "USED" || qr.CommandId == null) Fail(409, "Không có lệnh mở đang chờ phản hồi.");
        if (qr!.Purpose == "RECEIVE") {
            var slot = await db.cau_hinh_ca_hoc.FindAsync(session.ca_muon);
            if (slot == null) Fail(409, "Ca học không tồn tại.");
            // TEMP QR TEST: uncomment this condition together with Validate's window.
            // if (DateTime.Now < session.ngay_muon.Date.Add(slot!.gio_bat_dau).AddMinutes(-15) || DateTime.Now >= session.ngay_muon.Date.Add(slot.gio_ket_thuc))
            //     Fail(409, "Đã ngoài thời gian nhận đồ của ca đăng ký.");
        }
        var expected = await db.phieu_muon_thiet_bi.Where(e => e.phieu_muon_id == id).Select(e => new { equipmentId = e.thiet_bi_id, name = e.ten_thiet_bi, quantity = e.so_luong }).ToListAsync();
        try { await mqtt.PublishAsync($"backend/cabinet/{deviceId}/action", JsonSerializer.Serialize(new { id = id.ToString(), sessionId = id, room = deviceId, action = "open", purpose = qr!.Purpose, commandId = qr.CommandId, expiresAt = DateTime.UtcNow.AddSeconds(30), expectedEquipment = expected })); }
        catch { Fail(503, "Chưa gửi lại được lệnh. Vui lòng thử sau."); }
        return new { commandId = qr!.CommandId, message = "Đã gửi lại cùng lệnh mở. Chờ tủ xác nhận." };
    }

    public async Task DeviceResult(string roomId, QrDeviceResult result)
    {
        if (result.Event == "STATE") { await ReceiptState(roomId, result); return; }
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        var session = await LockSession(result.SessionId);
        if (!session.QrEnabled || session.ma_phong != roomId || session.trang_thai is "COMPLETED" or "CANCELED") return;
        var qr = await db.QrAccesses.FirstOrDefaultAsync(q => q.SessionId == session.id && q.CommandId == result.CommandId && q.State != "REVOKED");
        if (qr == null || qr.State == "ISSUED") return;
        var cabinet = await db.thiet_bi_iot.FirstOrDefaultAsync(c => c.ma_phong == roomId);
        if (cabinet == null) return;
        switch (result.Event) {
            case "OPEN_FAILED" when qr.State == "USED":
                qr.State = "FAILED"; session.QrPhase = qr.Purpose + "_QR_FAILED"; break;
            case "OPENED" when qr.State == "USED":
                qr.State = "OPENED"; session.is_cabinet_open = true; if (cabinet.trang_thai_khoa != "ERROR") cabinet.trang_thai_khoa = "UNLOCKED";
                session.QrPhase = qr.Purpose + "_OPEN"; break;
            case "CLOSED" when qr.State == "OPENED" || (!RequireReturnVerification && qr.Purpose == "RETURN" && qr.State == "CLOSED"):
                qr.State = "CLOSED"; session.is_cabinet_open = false; if (cabinet.trang_thai_khoa != "ERROR") cabinet.trang_thai_khoa = "LOCKED";
                if (qr.Purpose == "RECEIVE") { session.trang_thai = "IN_USE"; session.thoi_gian_nhan ??= DateTime.Now; session.QrPhase = "IN_USE"; }
                else session.QrPhase = "RETURN_CHECK";
                break;
            case "RFID_SCAN" when qr.State is "USED" or "OPENED" or "CLOSED":
                var uids = result.RfidUids;
                if (uids == null || uids.Length > 32 || result.RfidSequence != uids.Length ||
                    uids.Any(uid => !RfidSession.ValidUid(uid)) || uids.Distinct(StringComparer.Ordinal).Count() != uids.Length) return;
                var previous = RfidSession.ParseObserved(qr.RfidObservedJson);
                if (previous != null && (result.RfidSequence <= qr.RfidSequence || previous.Except(uids, StringComparer.Ordinal).Any())) return;
                qr.RfidObservedJson = JsonSerializer.Serialize(uids);
                qr.RfidSequence = result.RfidSequence;
                var state = RfidSession.Summarize(qr.RfidExpectedJson, qr.RfidObservedJson).Status;
                qr.RfidPassed = state == "MATCH" ? true : state == "WRONG" ? false : null;
                break;
            case "VERIFIED" when qr.Purpose == "RETURN" && qr.State is "OPENED" or "CLOSED":
                if (result.Sequence <= qr.VerificationSequence || !result.RfidPassed.HasValue || !result.CameraPassed.HasValue) return;
                qr.VerificationSequence = result.Sequence; qr.CameraPassed = result.CameraPassed; break;
            default: return;
        }
        if (qr.Purpose == "RETURN" && qr.State == "CLOSED") {
            session.QrPhase = qr.RfidPassed == false || qr.CameraPassed == false || session.tinh_trang_tra == "MAT_DO"
                ? "RETURN_FAILED" : "RETURN_CHECK";
            // Only the administrator's checked return endpoint may complete this session.
        }
        cabinet.trang_thai_mang = true; cabinet.lan_cuoi_online = DateTime.Now;
        Log(session, "QR_DEVICE_RESULT", null, new { result.CommandId, result.Event, result.RfidPassed, result.CameraPassed, result.Sequence, session.QrPhase, returnVerificationRequired = RequireReturnVerification });
        await db.SaveChangesAsync(); await tx.CommitAsync();
        await hub.Clients.All.SendAsync("CabinetStatusChanged", new { roomId, isOpen = session.is_cabinet_open == true, isOnline = true,
            status = session.trang_thai == "COMPLETED" ? "Trống" : "Đang mượn", sessionId = session.id, qrPhase = session.QrPhase });
    }
}
