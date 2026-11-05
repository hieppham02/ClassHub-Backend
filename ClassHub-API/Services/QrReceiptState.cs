using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using System.Text.Json;

namespace ClassHub_API.Services;

public partial class QrAccessService
{
    // One snapshot persists phase and UID observations atomically; ACK follows DB commit.
    private async Task ReceiptState(string room, QrDeviceResult result)
    {
        var uids = result.RfidUids;
        if (result.Revision is < 1 or > uint.MaxValue || result.Phase is not ("UNLOCKED" or "OPENED" or "CLOSED" or "FAILED") ||
            result.DoorOpened == null || (result.Phase is "OPENED" or "CLOSED" && result.DoorOpened != true) ||
            (result.Phase == "UNLOCKED" && result.DoorOpened != false) || uids == null || uids.Length > 32 ||
            result.RfidSequence != uids.Length || uids.Any(uid => !RfidSession.ValidUid(uid)) ||
            uids.Distinct(StringComparer.Ordinal).Count() != uids.Length) return;

        var changed = false;
        string? phase = null;
        bool open = false, released = false;
        await using (var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted))
        {
            var session = await LockSession(result.SessionId);
            if (!session.QrEnabled || session.ma_phong != room || session.trang_thai == "CANCELED") return;
            var qr = await db.QrAccesses.FirstOrDefaultAsync(q => q.SessionId == session.id && q.CommandId == result.CommandId);
            if (qr == null || qr.State is "ISSUED" or "REVOKED" || result.Revision < qr.DeviceRevision) return;
            if (qr.State == "CLOSED" && result.Phase != "CLOSED" || qr.State == "FAILED" && result.Phase != "FAILED") return;
            var previous = RfidSession.ParseObserved(qr.RfidObservedJson);
            if (previous != null && previous.Except(uids, StringComparer.Ordinal).Any()) return;
            if (result.Revision == qr.DeviceRevision)
            {
                if (qr.DevicePhase != result.Phase || previous == null || !previous.SequenceEqual(uids)) return;
                // Duplicate snapshot is acknowledged again without applying transitions twice.
            }
            else
            {
                if (session.trang_thai == "COMPLETED" && result.Phase != "CLOSED") return;
                qr.DeviceRevision = result.Revision; qr.DevicePhase = result.Phase;
                qr.RfidObservedJson = JsonSerializer.Serialize(uids); qr.RfidSequence = result.RfidSequence;
                var rfid = RfidSession.Summarize(qr.RfidExpectedJson, qr.RfidObservedJson).Status;
                qr.RfidPassed = rfid == "MATCH" ? true : rfid == "WRONG" ? false : null;
                if (session.trang_thai != "COMPLETED")
                {
                    var cabinet = await db.thiet_bi_iot.FirstOrDefaultAsync(c => c.ma_phong == room);
                    if (cabinet == null) return;
                    switch (result.Phase)
                    {
                        case "UNLOCKED":
                            qr.State = "USED"; session.QrPhase = qr.Purpose + "_UNLOCKED";
                            session.is_cabinet_open = false; if (cabinet.trang_thai_khoa != "ERROR") cabinet.trang_thai_khoa = "UNLOCKED"; break;
                        case "OPENED":
                            qr.State = "OPENED"; session.QrPhase = qr.Purpose + "_OPEN";
                            session.is_cabinet_open = true; if (cabinet.trang_thai_khoa != "ERROR") cabinet.trang_thai_khoa = "UNLOCKED"; break;
                        case "CLOSED":
                            qr.State = "CLOSED"; session.is_cabinet_open = false; if (cabinet.trang_thai_khoa != "ERROR") cabinet.trang_thai_khoa = "LOCKED";
                            if (qr.Purpose == "RECEIVE") { session.trang_thai = "IN_USE"; session.QrPhase = "IN_USE"; session.thoi_gian_nhan ??= DateTime.Now; }
                            else { session.trang_thai = "RETURNING"; session.QrPhase = rfid == "WRONG" || session.tinh_trang_tra == "MAT_DO" ? "RETURN_FAILED" : "RETURN_CHECK"; }
                            break;
                        case "FAILED":
                            qr.State = "FAILED"; session.QrPhase = qr.Purpose + "_QR_FAILED"; break;
                    }
                    cabinet.trang_thai_mang = true; cabinet.lan_cuoi_online = DateTime.Now;
                    Log(session, "QR_DEVICE_STATE", null, new { result.CommandId, result.Revision, result.Phase, result.DoorOpened, count = uids.Length, rfid });
                }
                await db.SaveChangesAsync();
                changed = true;
            }
            phase = session.QrPhase; open = session.is_cabinet_open == true;
            released = session.trang_thai == "COMPLETED" || qr.Purpose == "RECEIVE" && qr.State == "CLOSED";
            await tx.CommitAsync();
        }
        // A publish failure leaves the DB durable. Firmware retries and receives this ACK next time.
        var deviceId = await CabinetAddress.ForRoom(db, room);
        await mqtt.PublishAsync($"backend/cabinet/{deviceId}/action", JsonSerializer.Serialize(new {
            room = deviceId, sessionId = result.SessionId, commandId = result.CommandId, revision = result.Revision, released, action = "receipt_ack" }));
        if (changed) await hub.Clients.All.SendAsync("CabinetStatusChanged", new { roomId = room, sessionId = result.SessionId,
            isOpen = open, isOnline = true, qrPhase = phase, devicePhase = result.Phase, deviceRevision = result.Revision,
            status = await AllocationPolicy.IsBlocked(db, room) ? "Bảo trì" : phase == "IN_USE" ? "Đang mượn" : phase is "RETURN_CHECK" or "RETURN_FAILED" ? "Đang xác nhận trả" : "Đang thao tác" });
    }
}
