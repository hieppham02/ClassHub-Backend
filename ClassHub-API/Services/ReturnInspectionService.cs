using ClassHub_API.Data;
using ClassHub_API.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClassHub_API.Services;

public record ExpectedReturnItem(int SnapshotId, int? EquipmentId, string Name, int Quantity);
public record CheckedReturnItem(int SnapshotId, int Quantity);

public static class ReturnReviewRules
{
    public static bool ValidAiSuggestion(JsonElement result, IReadOnlyList<ExpectedReturnItem> expected)
    {
        try
        {
            var model = result.GetProperty("modelVersion").GetString();
            if (model?.Length != 64 || !model.All(Uri.IsHexDigit) || expected.Count == 0) return false;
            var items = result.GetProperty("items").EnumerateArray().ToList();
            if (items.Count != expected.Count || items.Select(x => x.GetProperty("snapshotId").GetInt32()).Distinct().Count() != expected.Count) return false;
            return expected.All(e => e.Quantity > 0 && items.Any(x => x.GetProperty("snapshotId").GetInt32() == e.SnapshotId &&
                x.GetProperty("expected").GetInt32() == e.Quantity && x.GetProperty("observed").GetInt32() == e.Quantity &&
                x.GetProperty("status").GetString() == "MATCH"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { return false; }
    }

    public static bool CompleteChecklist(IReadOnlyList<ExpectedReturnItem> expected, IReadOnlyList<CheckedReturnItem>? checkedItems) =>
        expected.Count > 0 && checkedItems != null && checkedItems.Count == expected.Count &&
        checkedItems.Select(x => x.SnapshotId).Distinct().Count() == expected.Count &&
        expected.All(e => e.Quantity > 0 && checkedItems.Any(x => x.SnapshotId == e.SnapshotId && x.Quantity == e.Quantity));
}

public class ReturnInspectionService(AppDbContext db, CabinetCamera camera, IHttpClientFactory clients,
    IConfiguration config, ILogger<ReturnInspectionService> logger)
{
    // Camera firmware supports one viewer; serialize captures in this API process.
    private static readonly SemaphoreSlim CaptureLock = new(1, 1);

    public async Task<List<ExpectedReturnItem>> Expected(int id, CancellationToken ct) =>
        await db.phieu_muon_thiet_bi.AsNoTracking().Where(e => e.phieu_muon_id == id).OrderBy(e => e.id)
            .Select(e => new ExpectedReturnItem(e.id, e.thiet_bi_id, e.ten_thiet_bi, e.so_luong)).ToListAsync(ct);

    public async Task<QrAccess> ClosedReturn(int id, CancellationToken ct)
    {
        var session = await db.phieu_muon.AsNoTracking().FirstOrDefaultAsync(s => s.id == id, ct);
        var qr = await db.QrAccesses.AsNoTracking().OrderByDescending(q => q.Id).FirstOrDefaultAsync(q => q.SessionId == id, ct);
        var cabinet = session == null ? null : await db.thiet_bi_iot.AsNoTracking().FirstOrDefaultAsync(c => c.ma_phong == session.ma_phong, ct);
        if (session?.trang_thai != "RETURNING" || session.is_cabinet_open == true || qr?.Purpose != "RETURN" ||
            qr.State != "CLOSED" || string.IsNullOrEmpty(qr.CommandId) || cabinet?.trang_thai_khoa != "LOCKED" || !cabinet.trang_thai_mang)
            throw new QrFlowException(409, "Chỉ kiểm tra sau khi tủ online và xác nhận đã mở rồi đóng cửa của đúng phiên trả.");
        return qr;
    }

    public async Task<ReturnInspection> Capture(int id, bool retry, CancellationToken ct)
    {
        if (!await CaptureLock.WaitAsync(0, ct)) throw new QrFlowException(409, "Đang chụp ảnh hộc. Vui lòng thử lại sau.");
        try
        {
            var qr = await ClosedReturn(id, ct);
            var previous = await db.ReturnInspections.OrderByDescending(i => i.Id).FirstOrDefaultAsync(i => i.CommandId == qr.CommandId, ct);
            if (previous != null && !retry) return previous;
            if (previous != null && DateTime.UtcNow - previous.CapturedAtUtc < TimeSpan.FromSeconds(10))
                throw new QrFlowException(429, "Đợi ít nhất 10 giây trước khi chụp lại.");
            if (await db.ReturnInspections.CountAsync(i => i.CommandId == qr.CommandId, ct) >= 30)
                throw new QrFlowException(429, "Đã đạt giới hạn 30 ảnh của lượt trả này. Liên hệ quản trị viên để kiểm tra trực tiếp.");
            var room = await db.phieu_muon.Where(s => s.id == id).Select(s => s.ma_phong).SingleAsync(ct);
            var expected = await Expected(id, ct);
            var inspection = new ReturnInspection { SessionId = id, CommandId = qr.CommandId!,
                ExpectedJson = JsonSerializer.Serialize(expected), Status = "REVIEW" };
            try
            {
                CabinetPhoto photo;
                try { photo = await camera.Capture(room, ct); }
                catch (System.Net.WebSockets.WebSocketException) when (!ct.IsCancellationRequested)
                {
                    // A brief disconnect can occur while ESP32 releases its previous viewer.
                    await Task.Delay(TimeSpan.FromMilliseconds(600), ct);
                    photo = await camera.Capture(room, ct);
                }
                inspection.CapturedAtUtc = DateTime.UtcNow;
                inspection.ImageJpeg = photo.Jpeg;
                inspection.ImageSha256 = Convert.ToHexString(SHA256.HashData(photo.Jpeg));
                inspection.CameraSequence = photo.Sequence;
                inspection.Reason = "Đã chụp ảnh mới. Chờ quản trị viên đối chiếu thiết bị.";
                await Analyze(inspection, expected, ct);
            }
            catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or System.Net.WebSockets.WebSocketException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                ct.ThrowIfCancellationRequested();
                inspection.Status = "REVIEW";
                inspection.Reason = ex is InvalidDataException ? ex.Message : inspection.ImageJpeg != null
                    ? "Đã chụp ảnh, nhưng dịch vụ AI chưa phản hồi. Quản trị viên cần đối chiếu thủ công."
                    : "Không lấy được ảnh. Kiểm tra camera/kết nối và chụp lại, hoặc kiểm tra trực tiếp.";
                logger.LogWarning("Return inspection capture/analysis unavailable for session {SessionId}: {Type}", id, ex.GetType().Name);
            }
            // Network work happens outside the transaction. Re-lock and revalidate correlation before persisting evidence.
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            var session = (await db.phieu_muon.FromSqlInterpolated($"SELECT * FROM borrowing_session WHERE id={id} FOR UPDATE").ToListAsync(ct)).Single();
            var current = await ClosedReturn(id, ct);
            if (current.CommandId != inspection.CommandId) throw new QrFlowException(409, "Phiên trả đã đổi; ảnh không được dùng cho phiên mới.");
            if (session.trang_thai != "RETURNING") throw new QrFlowException(409, "Phiên trả không còn chờ kiểm tra.");
            db.ReturnInspections.Add(inspection);
            db.nhat_ky_he_thong.Add(new NhatKyHeThong { hanh_dong = "RETURN_INSPECTION_CAPTURED", thoi_gian = DateTime.Now,
                user_agent = "ClassHub Return Verification", chi_tiet = JsonSerializer.Serialize(new { sessionId = id,
                    inspection.CommandId, inspection.ImageSha256, inspection.Status, inspection.Reason }) });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return inspection;
        }
        finally { CaptureLock.Release(); }
    }

    private async Task Analyze(ReturnInspection inspection, List<ExpectedReturnItem> expected, CancellationToken ct)
    {
        var address = config["ReturnVerification:VisionUrl"];
        if (string.IsNullOrWhiteSpace(address))
        {
            inspection.Reason = "Đã chụp ảnh, nhưng chưa cấu hình mô hình AI. Quản trị viên cần đối chiếu thủ công.";
            return;
        }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new InvalidDataException("Cấu hình dịch vụ AI không hợp lệ; cần quản trị viên kiểm tra.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.Add("X-ClassHub-Vision-Key", config["ReturnVerification:VisionKey"] ?? "");
        request.Content = JsonContent.Create(new { imageBase64 = Convert.ToBase64String(inspection.ImageJpeg!),
            sessionId = inspection.SessionId, commandId = inspection.CommandId, expected });
        using var response = await clients.CreateClient("return-vision").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var content = new MemoryStream();
        var buffer = new byte[4096];
        int received;
        while ((received = await stream.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (content.Length + received > 65536) throw new InvalidDataException("Phản hồi AI vượt dung lượng cho phép.");
            content.Write(buffer, 0, received);
        }
        var body = System.Text.Encoding.UTF8.GetString(content.ToArray());
        using var result = JsonDocument.Parse(body);
        var root = result.RootElement;
        // Bind the analysis to this specific request; malformed/unknown outcomes always require review.
        if (root.GetProperty("sessionId").GetInt32() != inspection.SessionId ||
            root.GetProperty("commandId").GetString() != inspection.CommandId)
            throw new InvalidDataException("Kết quả AI không khớp phiên trả.");
        if (!root.TryGetProperty("imageSha256", out var imageHash) ||
            !string.Equals(imageHash.GetString(), inspection.ImageSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Kết quả AI không khớp ảnh của phiên trả.");
        var status = root.GetProperty("status").GetString();
        inspection.Status = status == "PASS" && ReturnReviewRules.ValidAiSuggestion(root, expected) ? "PASS" : "REVIEW";
        inspection.AnalysisJson = body;
        var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
        inspection.Reason = string.IsNullOrWhiteSpace(reason) ? "AI đã phân tích; quản trị viên cần xác nhận." : reason;
        if (inspection.Reason.Length > 500) inspection.Reason = inspection.Reason[..500];
    }
}

public class ReturnInspectionWorker(IServiceScopeFactory scopes, ILogger<ReturnInspectionWorker> logger) : BackgroundService
{
    // Existing QR rows use utf8mb4_general_ci, while return_inspection uses
    // utf8mb4_unicode_ci. MariaDB rejects implicit column-to-column comparison.
    public static IQueryable<int> PendingSessionIds(AppDbContext db) =>
        db.QrAccesses.AsNoTracking().Where(q => q.Purpose == "RETURN" && q.State == "CLOSED" &&
            q.CommandId != null && db.phieu_muon.Any(s => s.id == q.SessionId && s.trang_thai == "RETURNING") &&
            !db.QrAccesses.Any(newer => newer.SessionId == q.SessionId && newer.Id > q.Id) &&
            !db.ReturnInspections.Any(i => i.SessionId == q.SessionId &&
                EF.Functions.Collate(i.CommandId, "utf8mb4_general_ci") == EF.Functions.Collate(q.CommandId!, "utf8mb4_general_ci")))
            .OrderBy(q => q.Id).Select(q => q.SessionId).Take(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var ids = await PendingSessionIds(db).ToListAsync(stoppingToken);
                foreach (var id in ids)
                {
                    try { await scope.ServiceProvider.GetRequiredService<ReturnInspectionService>().Capture(id, false, stoppingToken); }
                    catch (QrFlowException) { /* State changed or another capture is in progress; retry next tick. */ }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Không tạo được bằng chứng trả đồ. Kiểm tra schema return_inspection và cấu hình camera."); }
        }
    }
}
