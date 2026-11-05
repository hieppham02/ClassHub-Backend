using ClassHub_API.Controllers;
using ClassHub_API.Data;
using ClassHub_API.Hubs;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

if (args.Length == 3 && args[0] == "--camera")
{
    var cameraConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["ReturnVerification:Cameras:PROBE:WebSocketUrl"] = args[1]
    }).Build();
    var photo = await new CabinetCamera(cameraConfig).Capture("PROBE", CancellationToken.None);
    await File.WriteAllBytesAsync(args[2], photo.Jpeg);
    Console.WriteLine($"Camera capture OK: {photo.Jpeg.Length} JPEG bytes, sequence {photo.Sequence}");
    return;
}

var connection = Environment.GetEnvironmentVariable("CLASSHUB_RETURN_TEST_CONNECTION") ?? throw new Exception("Use run.py to create an isolated test database.");
var cs = new MySqlConnectionStringBuilder(connection);
if (!cs.Database.StartsWith("classhub_return_test_") || cs.Database.Length != 33) throw new Exception("Refusing a non-test database.");
var options = new DbContextOptionsBuilder<AppDbContext>().UseMySql(connection, ServerVersion.AutoDetect(connection)).Options;
var checks = 0;
void Check(bool pass, string message) { if (!pass) throw new Exception("FAIL: " + message); checks++; Console.WriteLine("PASS: " + message); }
async Task Throws(Func<Task> work, int status, string message)
{
    try { await work(); throw new Exception("Expected rejection: " + message); }
    catch (QrFlowException ex) { Check(ex.Status == status, message); }
}
var packet = new byte[22];
"OVF1"u8.CopyTo(packet); BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), 640);
BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), 480); BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 91);
packet[14] = 1; new byte[] { 255, 216, 1, 2, 255, 217 }.CopyTo(packet, 16);
Check(CabinetCamera.Decode(packet).Sequence == 91, "OVF1 JPEG and sequence decoded");
foreach (var kind in new[] { "raw", "truncated", "dimensions", "oversize" })
{
    var bad = (byte[])packet.Clone();
    if (kind == "raw") bad[14] = 0;
    if (kind == "truncated") bad = bad[..18];
    if (kind == "dimensions") bad[4] = 0;
    if (kind == "oversize") bad = new byte[CabinetCamera.MaxPacketBytes + 1];
    try { CabinetCamera.Decode(bad); throw new Exception("Accepted invalid packet"); }
    catch (InvalidDataException) { Check(true, "Reject " + kind + " camera packet"); }
}
var items = new List<ExpectedReturnItem> { new(1, 7, "Remote", 1), new(2, 8, "Microphone", 2) };
Check(ReturnReviewRules.CompleteChecklist(items, [new(1, 1), new(2, 2)]), "Exact checklist accepted");
Check(!ReturnReviewRules.CompleteChecklist(items, [new(1, 1), new(1, 2)]), "Duplicate snapshot cannot fill checklist");
Check(!ReturnReviewRules.CompleteChecklist(items, [new(1, 1)]), "Missing checklist item rejected");
Check(!ReturnReviewRules.CompleteChecklist([], []), "Empty inventory cannot complete return");

using (var valid = JsonDocument.Parse("""
{"modelVersion":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","items":[{"snapshotId":1,"expected":1,"observed":1,"status":"MATCH"},{"snapshotId":2,"expected":2,"observed":2,"status":"MATCH"}]}
""")) Check(ReturnReviewRules.ValidAiSuggestion(valid.RootElement, items), "AI recommendation must contain every snapshot quantity and model version");
using (var invalid = JsonDocument.Parse("""{"status":"PASS","items":[]}"""))
    Check(!ReturnReviewRules.ValidAiSuggestion(invalid.RootElement, items), "A bare AI PASS cannot replace a complete analysis");

var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSignalR();
builder.Services.AddHttpClient("return-vision");
builder.Services.AddDbContext<AppDbContext>(o => o.UseMySql(connection, ServerVersion.AutoDetect(connection)));
builder.Services.AddSingleton<CabinetCamera>();
builder.Services.AddScoped<ReturnInspectionService>();
builder.Services.AddSingleton<FakeMqtt>();
builder.Services.AddSingleton<IMqttService>(p => p.GetRequiredService<FakeMqtt>());
builder.Services.AddScoped<QrAccessService>();
builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => {});
builder.Services.AddAuthorization();
builder.Services.AddControllers().AddApplicationPart(typeof(ReturnInspectionController).Assembly);
await using var app = builder.Build();
Func<Task>? onFrame = null;
var remainingDisconnects = 0;
var requireCameraAuth = false;
var cameraLogins = 0;
const string cameraAuthToken = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Map("/ws", async context => {
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[128];
    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("STATE {}")), WebSocketMessageType.Text, true, CancellationToken.None);
    if (requireCameraAuth) {
        var frame = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        if (Encoding.UTF8.GetString(buffer, 0, frame.Count) != "AUTH " + cameraAuthToken) { ws.Abort(); return; }
    }
    for (var n = 0; n < 2; n++) await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
    if (remainingDisconnects > 0) { remainingDisconnects--; ws.Abort(); return; }
    if (onFrame != null) await onFrame();
    await ws.SendAsync(new ArraySegment<byte>(packet, 0, 10), WebSocketMessageType.Binary, false, CancellationToken.None);
    await ws.SendAsync(new ArraySegment<byte>(packet, 10, packet.Length - 10), WebSocketMessageType.Binary, true, CancellationToken.None);
    var close = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
    if (close.MessageType == WebSocketMessageType.Close) await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
});
app.MapPost("/auth/login", async (HttpContext context) => {
    using var input = await JsonDocument.ParseAsync(context.Request.Body);
    if (input.RootElement.GetProperty("password").GetString() != "FIXTURE-admin-password-123") return Results.Unauthorized();
    cameraLogins++;
    return Results.Json(new { token = cameraAuthToken, expiresIn = 1800 });
});
app.MapPost("/analyze", () => Results.Json(new { sessionId = 999999, commandId = "wrong", status = "PASS" }));
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
    ["ReturnVerification:Cameras:TEST-CV:WebSocketUrl"] = address.Replace("http:", "ws:") + "/ws"
}).Build();
var hub = app.Services.GetRequiredService<IHubContext<CabinetHub>>();
var factory = app.Services.GetRequiredService<IHttpClientFactory>();
AppDbContext Db() => new(options);
ReturnInspectionService Service(AppDbContext db) => new(db, new CabinetCamera(config), factory, config, app.Services.GetRequiredService<ILogger<ReturnInspectionService>>());
ControllerContext UserContext(string id, bool admin = false) => new() { HttpContext = new DefaultHttpContext {
    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Role, admin ? "ADMIN" : "USER") }, "test")) } };
OperationsController Ops(AppDbContext db) => new(db, hub) { ControllerContext = UserContext("ADMIN", true) };

async Task Seed(int id, string state = "OPENED")
{
    await using var db = Db();
    if (!await db.thiet_bi_iot.AnyAsync()) db.thiet_bi_iot.Add(new ThietBiIot { id = 1, ma_phong = "TEST-CV", ten_tu = "Test cabinet",
        mac_address = "00:00:00:00:00:01", mqtt_topic = "test/cabinet", trang_thai_mang = true, trang_thai_khoa = "LOCKED" });
    db.phieu_muon.Add(new PhieuMuon { id = id, ma_sv = "OWNER", ma_phong = "TEST-CV", ca_muon = 1,
        ngay_muon = DateTime.Today, trang_thai = "RETURNING", QrPhase = "RETURN_OPEN", is_cabinet_open = false });
    db.QrAccesses.Add(new QrAccess { SessionId = id, CommandId = id.ToString("x32"), TokenHash = id.ToString("x64"), Purpose = "RETURN", State = state,
        ExpiresAt = DateTime.UtcNow.AddMinutes(1) });
    db.phieu_muon_thiet_bi.Add(new PhieuMuonThietBi { phieu_muon_id = id, thiet_bi_id = 7, ten_thiet_bi = "Remote", so_luong = 1, tinh_trang_du_kien = "TOT" });
    await db.SaveChangesAsync();
}

await Seed(1001);
await using (var db = Db()) Check(await Ops(db).Confirm(1001, new ReturnReport { Note = "checked" }) is ConflictObjectResult, "Global locked state cannot bypass this return's close event");
await using (var db = Db()) await new QrAccessService(db, new FakeMqtt(), hub).DeviceResult("TEST-CV", new(1001, 1001.ToString("x32"), "CLOSED"));
await using (var db = Db()) Check((await db.phieu_muon.FindAsync(1001))!.trang_thai == "RETURNING", "Closing door never completes return");
await using (var db = Db()) await new QrAccessService(db, new FakeMqtt(), hub).DeviceResult("TEST-CV", new(1001, 1001.ToString("x32"), "VERIFIED", true, true, 1));
await using (var db = Db()) Check((await db.phieu_muon.FindAsync(1001))!.trang_thai == "RETURNING", "Device reporting both checks passed still requires review");
await using (var db = Db()) Check((await ReturnInspectionWorker.PendingSessionIds(db).ToListAsync()).Contains(1001),
    "Worker query finds closed return across different command ID collations");
int inspectionId, snapshotId;
await using (var db = Db())
{
    var inspection = await Service(db).Capture(1001, false, CancellationToken.None);
    inspectionId = inspection.Id;
    snapshotId = await db.phieu_muon_thiet_bi.Where(x => x.phieu_muon_id == 1001).Select(x => x.id).SingleAsync();
    Check(inspection.ImageJpeg?.Length == 6 && inspection.CameraSequence == 91 && inspection.Status == "REVIEW", "Capture saves fragmented new JPEG as correlated review evidence");
    var repeat = await Service(db).Capture(1001, false, CancellationToken.None);
    Check(repeat.Id == inspection.Id, "Worker does not duplicate existing evidence");
    Check(!(await ReturnInspectionWorker.PendingSessionIds(db).ToListAsync()).Contains(1001),
        "Worker query excludes return when this command already has evidence");
}
await using (var db = Db())
{
    var ctl = new ReturnInspectionController(db, Service(db)) { ControllerContext = UserContext("STRANGER") };
    Check(await ctl.Get(1001, CancellationToken.None) is NotFoundResult, "Unrelated user cannot read evidence");
    Check(await ctl.Capture(1001, CancellationToken.None) is NotFoundResult, "Unrelated user cannot trigger capture");
    ctl.ControllerContext = UserContext("OWNER");
    Check(await ctl.Get(1001, CancellationToken.None) is OkObjectResult, "Borrower can read own evidence");
    Check(ctl.Response.Headers.CacheControl == "no-store", "Evidence is not cached");
}
using (var http = new HttpClient { BaseAddress = new Uri(address) })
{
    http.DefaultRequestHeaders.Add("X-Test-User", "STRANGER");
    var hidden = await http.GetAsync("/api/operations/sessions/1001/inspection");
    Check(hidden.StatusCode == System.Net.HttpStatusCode.NotFound, "HTTP endpoint hides evidence from unrelated users");
    http.DefaultRequestHeaders.Remove("X-Test-User");
    http.DefaultRequestHeaders.Add("X-Test-User", "OWNER");
    var visible = await http.GetAsync("/api/operations/sessions/1001/inspection");
    Check(visible.IsSuccessStatusCode, "HTTP endpoint permits the borrower to read evidence");
    var forbidden = await http.PostAsJsonAsync("/api/operations/sessions/1001/confirm-return", new { inspectionId, note = "test" });
    Check(forbidden.StatusCode == System.Net.HttpStatusCode.Forbidden, "HTTP authorization rejects non-admin return confirmation");
}
await using (var db = Db()) Check(await Ops(db).Confirm(1001, new ReturnReport { InspectionId = inspectionId + 1, Note = "checked" }) is ConflictObjectResult, "Wrong inspection cannot authorize completion");
await using (var db = Db()) Check(await Ops(db).Confirm(1001, new ReturnReport { InspectionId = inspectionId }) is BadRequestObjectResult, "Reviewer must explain decision");
await using (var db = Db()) Check(await Ops(db).Confirm(1001, new ReturnReport { InspectionId = inspectionId, Note = "checked", CheckedItems = [new(snapshotId, 0)] }) is BadRequestObjectResult, "Missing quantity blocks completion");
await using (var db = Db())
{
    Check(await Ops(db).Confirm(1001, new ReturnReport { InspectionId = inspectionId, Note = "Remote missing", Condition = "MAT_DO" }) is OkObjectResult, "Missing equipment can be recorded");
    db.ChangeTracker.Clear();
    var session = (await db.phieu_muon.FindAsync(1001))!;
    Check(session.trang_thai == "RETURNING" && session.QrPhase == "RETURN_FAILED", "Missing return remains open for resolution");
}
await using (var db = Db())
{
    var evidence = (await db.ReturnInspections.FindAsync(inspectionId))!;
    evidence.CapturedAtUtc = DateTime.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
    var again = await Service(db).Capture(1001, true, CancellationToken.None);
    Check(again.Id != inspectionId && again.ReviewedAtUtc == null, "Fresh capture can follow a missing-items review");
    inspectionId = again.Id;
}
await using (var db = Db())
{
    Check(await Ops(db).Confirm(1001, new ReturnReport { InspectionId = inspectionId, Note = "New photo and inventory checked", CheckedItems = [new(snapshotId, 1)] }) is OkObjectResult, "Complete evidence and checklist permit authorized review");
    db.ChangeTracker.Clear();
    var session = (await db.phieu_muon.FindAsync(1001))!;
    Check(session.trang_thai == "COMPLETED" && session.QrPhase == "COMPLETED", "Reviewed return reaches COMPLETED");
    Check((await db.ReturnInspections.FindAsync(inspectionId))!.ReviewedBy == "ADMIN", "Reviewer identity is retained");
    await Throws(() => Service(db).Capture(1001, true, CancellationToken.None), 409, "Completed session cannot be recaptured");
}

await Seed(1002, "CLOSED");
onFrame = async () => {
    await using var db = Db();
    var qr = await db.QrAccesses.SingleAsync(q => q.SessionId == 1002);
    qr.State = "REVOKED";
    db.QrAccesses.Add(new QrAccess { SessionId = 1002, CommandId = 2002.ToString("x32"), TokenHash = 2002.ToString("x64"), Purpose = "RETURN", State = "ISSUED", ExpiresAt = DateTime.UtcNow.AddMinutes(1) });
    await db.SaveChangesAsync();
};
await using (var db = Db()) await Throws(() => Service(db).Capture(1002, false, CancellationToken.None), 409, "QR changed during capture rejects stale photo");
await using (var db = Db()) Check(!await db.ReturnInspections.AnyAsync(i => i.SessionId == 1002), "Stale photo was not persisted");
onFrame = null;
await Seed(1003, "CLOSED");
config["ReturnVerification:VisionUrl"] = address + "/analyze";
await using (var db = Db())
{
    var result = await Service(db).Capture(1003, false, CancellationToken.None);
    Check(result.ImageJpeg != null && result.Status == "REVIEW", "Wrong-session AI result preserves photo and requires review");
}
await Seed(1004, "CLOSED");
config["ReturnVerification:Cameras:TEST-CV:WebSocketUrl"] = null;
int noPhotoId, noPhotoSnapshot;
await using (var db = Db())
{
    var failed = await Service(db).Capture(1004, false, CancellationToken.None);
    noPhotoId = failed.Id;
    noPhotoSnapshot = await db.phieu_muon_thiet_bi.Where(x => x.phieu_muon_id == 1004).Select(x => x.id).SingleAsync();
    Check(failed.ImageJpeg == null && failed.Status == "REVIEW", "Camera failure produces an explicit review record");
}
await using (var db = Db())
{
    var report = new ReturnReport { InspectionId = noPhotoId, Note = "Direct physical check", CheckedItems = [new(noPhotoSnapshot, 1)] };
    Check(await Ops(db).Confirm(1004, report) is ConflictObjectResult, "No photo cannot be approved without explicit physical check");
    report.PhysicalCheck = true;
    Check(await Ops(db).Confirm(1004, report) is OkObjectResult, "Admin may explicitly document physical check when camera fails");
    db.ChangeTracker.Clear();
    var reviewed = (await db.ReturnInspections.FindAsync(noPhotoId))!;
    Check(reviewed.ImageJpeg == null && reviewed.ReviewJson!.Contains("PhysicalCheck\":true"), "Fallback is recorded honestly without fabricated image");
}
await Seed(1005, "CLOSED");
config["ReturnVerification:Cameras:TEST-CV:WebSocketUrl"] = address.Replace("http:", "ws:") + "/ws";
config["ReturnVerification:VisionUrl"] = null;
remainingDisconnects = 1;
await using (var db = Db())
{
    var result = await Service(db).Capture(1005, false, CancellationToken.None);
    Check(remainingDisconnects == 0 && result.ImageJpeg != null, "One transient WebSocket disconnect is retried safely");
}

var expectedRfid = JsonSerializer.Serialize(new[] { new RfidExpectedItem(1, 7, "Remote", 1, ["11223344"]) });
Check(RfidSession.Summarize(expectedRfid, null).Status == "NO_DATA", "Unreceived RFID data is not a pass");
Check(RfidSession.Summarize(expectedRfid, "[]").Status == "INCOMPLETE", "Empty scan means not yet scanned");
Check(RfidSession.Summarize(expectedRfid, "[\"11223344\"]").Status == "MATCH", "Exact assigned UID matches");
Check(RfidSession.Summarize(expectedRfid, "[\"AABBCCDD\"]").Status == "WRONG", "UID outside snapshot is identified");
Check(RfidSession.Summarize(null, "[\"11223344\"]").Status == "UNCONFIGURED", "No assignment never fabricates a match");
Check(!RfidSession.ValidUid("1122") && !RfidSession.ValidUid("GG223344") && RfidSession.ValidUid("11223344"), "UID syntax is bounded and canonical");

await Seed(1101, "CLOSED");
await using (var db = Db())
{
    var qr = await db.QrAccesses.SingleAsync(q => q.SessionId == 1101);
    qr.RfidExpectedJson = expectedRfid; await db.SaveChangesAsync();
}
async Task Scan(string[] uids, int sequence) {
    await using var db = Db();
    await new QrAccessService(db, new FakeMqtt(), hub).DeviceResult("TEST-CV",
        new(1101, 1101.ToString("x32"), "RFID_SCAN", RfidUids: uids, RfidSequence: sequence));
}
await Scan(["11223344"], 1);
await using (var db = Db())
{
    var qr = await db.QrAccesses.SingleAsync(q => q.SessionId == 1101);
    Check(qr.RfidPassed == true && qr.RfidSequence == 1, "Correlated scan persists matched evidence");
    Check((await db.phieu_muon.FindAsync(1101))!.trang_thai == "RETURNING", "RFID match alone cannot complete return");
}
await Scan(["11223344"], 1);
await Scan([], 0);
await using (var db = Db()) Check((await db.QrAccesses.SingleAsync(q => q.SessionId == 1101)).RfidSequence == 1, "Duplicate and late smaller snapshots cannot roll back RFID evidence");
await Scan(["11223344", "AABBCCDD"], 2);
await Scan(["11223344", "11223344"], 2);
await using (var db = Db())
{
    var qr = await db.QrAccesses.SingleAsync(q => q.SessionId == 1101);
    Check(qr.RfidPassed == false && RfidSession.Summarize(qr.RfidExpectedJson, qr.RfidObservedJson).Unexpected.Single() == "AABBCCDD", "Unexpected tag is retained, duplicate malformed snapshot rejected");
}
await using (var db = Db()) await new QrAccessService(db, new FakeMqtt(), hub).DeviceResult("WRONG-ROOM",
    new(1101, 1101.ToString("x32"), "RFID_SCAN", RfidUids: ["11223344", "AABBCCDD", "55667788"], RfidSequence: 3));
await using (var db = Db()) Check((await db.QrAccesses.SingleAsync(q => q.SessionId == 1101)).RfidSequence == 2, "RFID event from wrong cabinet cannot modify session");
await using (var db = Db()) await new QrAccessService(db, new FakeMqtt(), hub).DeviceResult("TEST-CV",
    new(1101, 1101.ToString("x32"), "VERIFIED", true, true, 99));
await using (var db = Db()) Check((await db.QrAccesses.SingleAsync(q => q.SessionId == 1101)).RfidPassed == false, "Legacy boolean VERIFIED cannot overwrite UID mismatch");
using (var http = new HttpClient { BaseAddress = new Uri(address) })
{
    http.DefaultRequestHeaders.Add("X-Test-User", "OWNER");
    var denied = await http.PostAsJsonAsync("/api/operations/equipment/7/rfid", new { uid = "11223344" });
    Check(denied.StatusCode == System.Net.HttpStatusCode.Forbidden, "Borrower cannot register RFID tags");
    var response = await http.GetFromJsonAsync<JsonElement>("/api/operations/sessions/1101/inspection");
    Check(response.GetProperty("rfid").GetProperty("status").GetString() == "WRONG", "HTTP status exposes wrong and missing tags for correct session");
    http.DefaultRequestHeaders.Add("X-Test-Role", "ADMIN");
    await using (var db = Db()) {
        db.thiet_bi.Add(new ThietBi { id = 7, ten_thiet_bi = "Remote", so_luong = 1, ma_phong = "TEST-CV", CatalogId = 1 });
        await db.SaveChangesAsync();
    }
    var added = await http.PostAsJsonAsync("/api/operations/equipment/7/rfid", new { uid = "11223344" });
    Check(added.IsSuccessStatusCode, "Admin can bind one physical UID");
    var duplicate = await http.PostAsJsonAsync("/api/operations/equipment/7/rfid", new { uid = "11223344" });
    Check(duplicate.StatusCode == System.Net.HttpStatusCode.Conflict, "Duplicate UID binding is rejected");
    var excess = await http.PostAsJsonAsync("/api/operations/equipment/7/rfid", new { uid = "AABBCCDD" });
    Check(excess.StatusCode == System.Net.HttpStatusCode.Conflict, "Bindings cannot exceed inventory quantity");
    await using (var db = Db()) {
        var snapshot = await RfidSession.Snapshot(db, 1101);
        Check(snapshot.Contains("11223344"), "Equipment binding is captured in immutable expected snapshot");
    }
}

await Seed(1201, "USED");
var ackTransport = new FakeMqtt();
async Task State(int id, string phase, long revision, bool opened, string[]? scanned = null) {
    await using var db = Db();
    await new QrAccessService(db, ackTransport, hub).DeviceResult("TEST-CV",
        new(id, id.ToString("x32"), "STATE", RfidUids: scanned ?? [], RfidSequence: scanned?.Length ?? 0,
            Revision: revision, Phase: phase, DoorOpened: opened));
}
await State(1201, "UNLOCKED", 1, false);
using (var http = new HttpClient { BaseAddress = new Uri(address) }) {
    http.DefaultRequestHeaders.Add("X-Test-User", "OWNER");
    var progress = await http.GetFromJsonAsync<JsonElement>("/api/operations/sessions/1201/progress");
    Check(progress.GetProperty("relayTriggered").GetBoolean() && !progress.GetProperty("doorOpened").GetBoolean(), "Reloaded progress distinguishes relay from measured opening");
}
await using (var db = Db()) {
    var session = (await db.phieu_muon.FindAsync(1201))!;
    Check(session.QrPhase == "RETURN_UNLOCKED" && session.is_cabinet_open == false, "Relay acknowledgement is distinct from physical door opening");
}
await State(1201, "OPENED", 2, true);
await using (var db = Db()) Check((await db.phieu_muon.FindAsync(1201))!.is_cabinet_open == true, "Physical open event updates measured door state");
ackTransport.FailPublish = true;
try { await State(1201, "CLOSED", 3, true); throw new Exception("Expected ACK transport failure"); }
catch (InvalidOperationException) { Check(true, "Simulated failure publishing post-commit ACK"); }
await using (var db = Db()) Check((await db.QrAccesses.SingleAsync(q => q.SessionId == 1201)).DeviceRevision == 3 &&
    (await db.phieu_muon.FindAsync(1201))!.trang_thai == "RETURNING", "DB snapshot survives lost ACK without completing return");
ackTransport.FailPublish = false;
int logsBefore;
await using (var db = Db()) logsBefore = await db.nhat_ky_he_thong.CountAsync();
await State(1201, "CLOSED", 3, true);
Check(ackTransport.Messages.Last().Payload.Contains("receipt_ack") && ackTransport.Messages.Last().Payload.Contains("\"revision\":3"), "Duplicate snapshot receives matching ACK again");
await using (var db = Db()) Check(await db.nhat_ky_he_thong.CountAsync() == logsBefore, "Duplicate snapshot does not duplicate business transition or audit");
var acksBefore = ackTransport.Messages.Count;
await State(1201, "OPENED", 2, true);
await State(1201, "CLOSED", 4, false);
await using (var db = Db()) Check((await db.QrAccesses.SingleAsync(q => q.SessionId == 1201)).DeviceRevision == 3 && ackTransport.Messages.Count == acksBefore,
    "Late event and unobserved door cycle cannot regress or fabricate closing");
await Seed(1202, "USED");
await State(1202, "CLOSED", 4, true, ["11223344"]);
await using (var db = Db()) Check((await db.QrAccesses.SingleAsync(q => q.SessionId == 1202)).State == "CLOSED" &&
    (await db.phieu_muon.FindAsync(1202))!.trang_thai == "RETURNING", "Latest durable snapshot recovers when intermediate events were lost");
await using (var db = Db()) {
    var session = (await db.phieu_muon.FindAsync(1202))!; session.trang_thai = "COMPLETED"; session.QrPhase = "COMPLETED";
    await db.SaveChangesAsync();
}
await State(1202, "CLOSED", 4, true, ["11223344"]);
await using (var db = Db()) Check((await db.phieu_muon.FindAsync(1202))!.trang_thai == "COMPLETED", "Late retry after admin completion is acknowledged without reopening session");

using (var http = new HttpClient { BaseAddress = new Uri(address) }) {
    http.DefaultRequestHeaders.Add("X-Test-User", "OWNER");
    var progress = await http.GetFromJsonAsync<JsonElement>("/api/operations/sessions/1201/progress");
    Check(progress.GetProperty("doorClosed").GetBoolean() && progress.GetProperty("needsReview").GetBoolean() && !progress.GetProperty("completed").GetBoolean(), "Closed return shows review pending, never fake completion");
    http.DefaultRequestHeaders.Remove("X-Test-User"); http.DefaultRequestHeaders.Add("X-Test-User", "STRANGER");
    Check((await http.GetAsync("/api/operations/sessions/1201/progress")).StatusCode == System.Net.HttpStatusCode.NotFound, "Progress endpoint enforces session ownership");
}

var measuredNow = DateTime.UtcNow;
CabinetHealthPayload Health(long boot = 1, long sequence = 1, long uptime = 1000) => new() {
    SchemaVersion = 1, Source = "REAL", BootId = boot == 1 ? new string('a', 32) : new string('b', 32),
    BootSequence = boot, Sequence = sequence, UptimeMs = uptime, FirmwareId = "TEST-FIXTURE", FirmwareVersion = "test",
    ClockSynced = true, MeasuredAtUtc = measuredNow, Rssi = -55, HeapFree = 100000, HeapLargest = 50000,
    PsramFree = 5000000, ReaderInitialized = true, LastRfidScanAgeMs = -1, CameraRecentFrame = true,
    CameraFrameAgeMs = 50, CameraFps100 = 1200, DoorOpen = false, SecurityProvisioned = true, ApOpen = true
};
await using (var db = Db()) {
    Check(await CabinetHealthService.Accept(db, "TEST-CV", Health(), measuredNow), "Measured health snapshot accepted");
    await db.SaveChangesAsync();
    var row = (await db.CabinetHealths.FindAsync("TEST-CV"))!;
    Check(CabinetHealthService.View(row, measuredNow)!.Metrics.ApOpen == true, "Open Wi-Fi AP state survives health persistence and is visible to admin");
    Check(CabinetHealthService.View(row, measuredNow)!.Stale == false && CabinetHealthService.View(row, measuredNow.AddSeconds(46))!.Stale,
        "Health becomes stale even without another heartbeat");
    Check(!await CabinetHealthService.Accept(db, "TEST-CV", Health(), measuredNow.AddSeconds(10)), "Duplicate health cannot refresh last-seen freshness");
    Check(row.ReceivedAtUtc == measuredNow, "Rejected duplicate leaves receive timestamp unchanged");
    Check(await CabinetHealthService.Accept(db, "TEST-CV", Health(sequence:2), measuredNow.AddSeconds(1)), "Different health events within one millisecond retain ordering");
    await db.SaveChangesAsync();
    Check(await CabinetHealthService.Accept(db, "TEST-CV", Health(boot:2,uptime:10), measuredNow.AddSeconds(2)), "New persistent boot sequence allows uptime reset");
    await db.SaveChangesAsync();
    Check(!await CabinetHealthService.Accept(db, "TEST-CV", Health(sequence:3,uptime:2000), measuredNow.AddSeconds(3)), "Retained heartbeat from old boot cannot replace new boot");
    var unsynced = Health(boot:2,sequence:2,uptime:20); unsynced.MeasuredAtUtc = DateTime.SpecifyKind(measuredNow, DateTimeKind.Unspecified);
    Check(await CabinetHealthService.Accept(db, "TEST-CV", unsynced, measuredNow.AddSeconds(4)) && !unsynced.ClockSynced && unsynced.MeasuredAtUtc == null,
        "Ambiguous time is marked unsynchronized instead of inventing UTC");
    await db.SaveChangesAsync();
    var mock = Health(boot:2,sequence:3,uptime:30); mock.Source="MOCK";
    Check(!await CabinetHealthService.Accept(db, "TEST-CV", mock, measuredNow), "Mock health never enters real telemetry");
    var invalid = Health(boot:2,sequence:3,uptime:30); invalid.Rssi=999;
    Check(!await CabinetHealthService.Accept(db, "TEST-CV", invalid, measuredNow), "Invalid health measurements rejected");
}
using (var http = new HttpClient { BaseAddress = new Uri(address) }) {
    http.DefaultRequestHeaders.Add("X-Test-User", "OWNER");
    Check((await http.GetAsync("/api/admin/cabinet-health/TEST-CV")).StatusCode == System.Net.HttpStatusCode.Forbidden, "Device diagnostics require administrator role");
    http.DefaultRequestHeaders.Add("X-Test-Role", "ADMIN");
    var response = await http.GetFromJsonAsync<JsonElement>("/api/admin/cabinet-health/TEST-CV");
    Check(response.GetProperty("health").GetProperty("metrics").GetProperty("readerInitialized").GetBoolean(), "Admin API exposes measured reader initialization without claiming an RF test");
}

await using (var db = Db()) {
    foreach (var person in new[] { ("ADMIN", "ADMIN"), ("TECH", "ADMIN"), ("OWNER", "SINHVIEN") })
        if (!await db.tai_khoan.AnyAsync(a => a.ma_sv == person.Item1)) db.tai_khoan.Add(new TaiKhoan {
            ma_sv=person.Item1, email=person.Item1+"@fixture.invalid", ho_ten="Fixture "+person.Item1,
            mat_khau="TEST-FIXTURE-NOT-LOGIN", vai_tro=person.Item2, trang_thai=true });
    db.phong_hoc.Add(new PhongHoc { ma_phong="INCIDENT-TEST", ten_phong="Fixture location", tang=1 });
    db.thiet_bi_iot.Add(new ThietBiIot { id=2, ma_phong="INCIDENT-TEST", ten_tu="Fixture incident cabinet", mac_address="00:00:00:00:00:02",
        mqtt_topic="fixture/incident", trang_thai_mang=true, trang_thai_khoa="LOCKED" });
    db.thiet_bi.Add(new ThietBi { id=8, ma_phong="INCIDENT-TEST", ten_thiet_bi="Fixture equipment", so_luong=1, CatalogId=1 });
    db.phieu_muon.Add(new PhieuMuon { id=1301, ma_sv="OWNER", ma_phong="INCIDENT-TEST", ca_muon=1, ngay_muon=DateTime.Today, trang_thai="PENDING" });
    await db.SaveChangesAsync();
}
int incidentId;
using (var http = new HttpClient { BaseAddress = new Uri(address) }) {
    http.DefaultRequestHeaders.Add("X-Test-User", "OWNER");
    var created = await http.PostAsJsonAsync("/api/damage-reports", new { roomId="INCIDENT-TEST", equipmentId=8, sessionId=1301,
        equipmentName="Fixture equipment", description="Fixture incident needs inspection" });
    Check(created.StatusCode == System.Net.HttpStatusCode.Created, "Borrower can link own session and equipment to a report");
    incidentId=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    var forbidden = await http.PatchAsJsonAsync($"/api/damage-reports/{incidentId}/workflow", new { status="RESOLVED",note="fixture" });
    Check(forbidden.StatusCode == System.Net.HttpStatusCode.Forbidden, "Borrower cannot resolve or release allocation hold");
    http.DefaultRequestHeaders.Remove("X-Test-User"); http.DefaultRequestHeaders.Add("X-Test-User", "ADMIN"); http.DefaultRequestHeaders.Add("X-Test-Role", "ADMIN");
    var assigned=await http.PatchAsJsonAsync($"/api/damage-reports/{incidentId}/workflow", new { assigneeId="TECH", status="IN_PROGRESS",allocationBlocked=true,note="Assigned fixture repair" });
    Check(assigned.IsSuccessStatusCode, "Admin assigns active repair owner and enables allocation hold");
    await using (var db = Db()) {
        db.nhat_ky_he_thong.Add(new NhatKyHeThong { hanh_dong="DAMAGE_REPORT", chi_tiet="invalid json", thoi_gian=DateTime.Now, user_agent="Fixture malformed report" });
        await db.SaveChangesAsync();
        var blocked = await AllocationPolicy.BlockedRooms(db);
        Check(blocked.Contains("INCIDENT-TEST") && !blocked.Contains(""), "Bulk allocation policy handles malformed JSON and identifies held room");
    }
    var cabinetsResponse = await http.GetAsync("/api/admin/cabinets");
    Check(cabinetsResponse.IsSuccessStatusCode, "Admin cabinet list endpoint succeeds with allocation hold and malformed legacy report");
    var cabinetRows = await cabinetsResponse.Content.ReadFromJsonAsync<JsonElement>();
    Check(cabinetRows.EnumerateArray().Any(c => c.GetProperty("roomId").GetString()=="INCIDENT-TEST" && c.GetProperty("allocationBlocked").GetBoolean()),
        "Admin cabinet list exposes the held room");
    await using (var db = Db()) {
        Check(await AllocationPolicy.IsBlocked(db,"INCIDENT-TEST"), "Allocation hold is durable business state");
        await Throws(()=>new QrAccessService(db,new FakeMqtt(),hub).Issue(1301,"OWNER","RECEIVE"),409,"Incident hold prevents a new receive QR before publishing");
    }
    var resolved=await http.PatchAsJsonAsync($"/api/damage-reports/{incidentId}/resolve",new {note="Fixture repair completed, pending readiness"});
    Check(resolved.IsSuccessStatusCode,"Existing resolve API remains compatible with workflow history");
    await using(var db=Db()) Check(await AllocationPolicy.IsBlocked(db,"INCIDENT-TEST"),"Marking resolved never automatically clears allocation hold");
    var unverified=await http.PatchAsJsonAsync($"/api/damage-reports/{incidentId}/workflow",new {status="RESOLVED",allocationBlocked=false,note="Attempt release without verification"});
    Check(unverified.StatusCode==System.Net.HttpStatusCode.Conflict,"Release requires explicit readiness verification");
    await using(var db=Db()) { var cab=(await db.thiet_bi_iot.FindAsync(2))!;cab.trang_thai_mang=false;await db.SaveChangesAsync(); }
    var offline=await http.PatchAsJsonAsync($"/api/damage-reports/{incidentId}/workflow",new {status="RESOLVED",allocationBlocked=false,verifiedReady=true,note="Fixture readiness"});
    Check(offline.StatusCode==System.Net.HttpStatusCode.Conflict,"Offline cabinet cannot be released for allocation");
    await using(var db=Db()) { var cab=(await db.thiet_bi_iot.FindAsync(2))!;cab.trang_thai_mang=true;var item=(await db.thiet_bi.FindAsync(8))!;item.tinh_trang="HONG";await db.SaveChangesAsync(); }
    var broken=await http.PatchAsJsonAsync($"/api/damage-reports/{incidentId}/workflow",new {status="RESOLVED",allocationBlocked=false,verifiedReady=true,note="Fixture readiness"});
    Check(broken.StatusCode==System.Net.HttpStatusCode.Conflict,"Broken equipment prevents release");
    await using(var db=Db()) { (await db.thiet_bi.FindAsync(8))!.tinh_trang="TOT";await db.SaveChangesAsync(); }
    var released=await http.PatchAsJsonAsync($"/api/damage-reports/{incidentId}/workflow",new {status="RESOLVED",allocationBlocked=false,verifiedReady=true,note="Direct readiness inspection completed"});
    Check(released.IsSuccessStatusCode,"Resolved incident is released only through explicit verified action");
    await using(var db=Db()) {
        Check(!await AllocationPolicy.IsBlocked(db,"INCIDENT-TEST"),"Verified release restores allocation policy");
        var report=(await db.nhat_ky_he_thong.FindAsync(incidentId))!;var detail=AllocationPolicy.Parse(report.chi_tiet)!;
        Check(detail.AssigneeId=="TECH"&&detail.SessionId==1301&&detail.EquipmentId==8&&detail.History.Count>=4,"Owner, links and repair history are preserved");
        await new QrAccessService(db,new FakeMqtt(),hub).Issue(1301,"OWNER","RECEIVE");
        Check(await db.QrAccesses.AnyAsync(q=>q.SessionId==1301&&q.State=="ISSUED"),"New receive QR is permitted after release");
    }
}
await using(var db=Db()) { (await db.thiet_bi_iot.FindAsync(2))!.trang_thai_khoa="ERROR";await db.SaveChangesAsync(); }
var statusProcessor = new BackgroundServices(new FakeMqtt(),app.Services.GetRequiredService<IServiceScopeFactory>(),hub,app.Services.GetRequiredService<ILogger<BackgroundServices>>());
await statusProcessor.ProcessMqttStatusMessageAsync("iot/cabinet/INCIDENT-TEST/status", "{\"room\":\"INCIDENT-TEST\",\"isOnline\":true,\"lockState\":\"LOCKED\",\"doorOpen\":false}");
await using(var db=Db()) Check((await db.thiet_bi_iot.FindAsync(2))!.trang_thai_khoa=="ERROR","Heartbeat cannot erase a persistent cabinet error/maintenance flag");
await using(var db=Db()) {
    var controller=new AdminCabinetsController(db,new FakeMqtt(),hub,app.Services.GetRequiredService<ILogger<AdminCabinetsController>>()){ControllerContext=UserContext("ADMIN",true)};
    Check(await controller.ToggleMaintenance(2) is BadRequestObjectResult,"Restoring cabinet error requires readiness evidence");
    Check(await controller.ToggleMaintenance(2,new AdminCabinetsController.MaintenanceRequest{VerifiedReady=true,Note="Fixture physical repair and inspection"}) is OkObjectResult,"Explicit inspected maintenance recovery clears error");
}

// Operational telemetry fixture: all measurements below exist only in the disposable test database.
var opsNow=DateTime.UtcNow;
await using(var db=Db()) {
    if(!await db.phong_hoc.AnyAsync(r=>r.ma_phong=="TEST-CV")) db.phong_hoc.Add(new PhongHoc{ma_phong="TEST-CV",ten_phong="Fixture cabinet room",tang=1});
    db.IotNodes.AddRange(new IotNode{Id=9001,NodeCode="FIXTURE-REAL",RoomId="TEST-CV",Name="Fixture real node",DataSource="REAL",PublishIntervalSeconds=30},
        new IotNode{Id=9002,NodeCode="FIXTURE-MOCK",RoomId="TEST-CV",Name="Fixture mock node",DataSource="MOCK"});
    await db.SaveChangesAsync();
    var message=new NodeMessage{SchemaVersion=1,Source="REAL",NodeCode="FIXTURE-REAL",RoomId="TEST-CV",BootId="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",BootSequence=1,Sequence=1,
        ClockSynced=true,MeasuredAtUtc=opsNow,Readings=[new("current","A",2.5,"GOOD")]};
    string Body()=>JsonSerializer.Serialize(message,NodeTelemetry.Json);
    Check(await NodeTelemetry.Accept(db,"iot/node/FIXTURE-REAL/telemetry",Body(),opsNow),"Registered real sensor message is persisted");
    Check(!await NodeTelemetry.Accept(db,"iot/node/FIXTURE-REAL/telemetry",Body(),opsNow.AddSeconds(30)),"Duplicate sensor packet cannot refresh last seen");
    Check(!await NodeTelemetry.Accept(db,"iot/node/OTHER/telemetry",Body(),opsNow),"Sensor topic identity must match registered node");
    message.Source="MOCK";Check(!await NodeTelemetry.Accept(db,"iot/node/FIXTURE-REAL/telemetry",Body(),opsNow),"MOCK message cannot enter REAL stream");message.Source="REAL";
    message.NodeCode="FIXTURE-MOCK";Check(!await NodeTelemetry.Accept(db,"iot/node/FIXTURE-MOCK/telemetry",Body(),opsNow),"REAL label cannot convert a demo node");message.NodeCode="FIXTURE-REAL";
    message.Sequence=2;message.Readings=[new("current","W",2.5,"GOOD")];Check(!NodeTelemetry.Valid(message,opsNow),"Current requires amperes, not invented watts");
    message.Readings=[new("current","A",null,"GOOD")];Check(!NodeTelemetry.Valid(message,opsNow),"GOOD reading requires a measured value");
    message.Readings=[new("current","A",2.5,"GOOD")];message.BootSequence=2;message.BootId="bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";message.Sequence=1;
    Check(await NodeTelemetry.Accept(db,"iot/node/FIXTURE-REAL/telemetry",Body(),opsNow),"New sensor boot accepts sequence restart");
    message.BootSequence=1;message.BootId="aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";message.Sequence=99;
    Check(!await NodeTelemetry.Accept(db,"iot/node/FIXTURE-REAL/telemetry",Body(),opsNow),"Old boot cannot overwrite fresh sensor readings");
    var node=(await db.IotNodes.FindAsync(9001))!;
    var noThreshold=await OperationRules.Current(db,config,node,opsNow);
    Check(noThreshold.State=="NOT_CONFIGURED","Uncalibrated current threshold never invents consumption alert");
    config["Operations:CurrentThresholdAmperes:FIXTURE-REAL"]="1.5";
    // Finish fixture sessions so the current rule can use a real completed-return context.
    foreach(var session in await db.phieu_muon.Where(x=>x.ma_phong=="TEST-CV").ToListAsync()) {session.trang_thai="COMPLETED";session.thoi_gian_tra=opsNow.AddHours(7).AddMinutes(-10);}
    db.IotSensors.RemoveRange(await db.IotSensors.Where(x=>x.NodeId==9001).ToListAsync());await db.SaveChangesAsync();
    for(int i=0;i<=10;i++) {var at=opsNow.AddSeconds(-300+i*30);db.IotSensors.Add(new IotSensor{NodeId=9001,BootId="bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",Sequence=(ulong)(100+i),DataSource="REAL",ReceivedAt=at,MeasuredAt=at,ClockSynced=true,
        ReadingsJson="[{\"metric\":\"current\",\"unit\":\"A\",\"value\":2.5,\"quality\":\"GOOD\"}]"});}
    await db.SaveChangesAsync();
    Check((await OperationRules.Current(db,config,node,opsNow)).State=="AFTER_RETURN_CURRENT","Sustained calibrated current after completed return triggers rule");
    Check((await OperationRules.Current(db,config,node,opsNow.AddMinutes(5))).State=="NO_DATA","Stale readings never produce current alert");
    var middle=await db.IotSensors.Where(x=>x.NodeId==9001).OrderBy(x=>x.Id).Skip(5).FirstAsync();middle.ReadingsJson="[{\"metric\":\"current\",\"unit\":\"A\",\"value\":2.5,\"quality\":\"UNCALIBRATED\"}]";await db.SaveChangesAsync();
    Check((await OperationRules.Current(db,config,node,opsNow)).State=="OBSERVING","Bad-quality sample breaks the sustained-current window");
    var old=Health(boot:4,sequence:1,uptime:1000);old.MeasuredAtUtc=opsNow.AddSeconds(-150);old.DoorOpen=true;
    Check(await CabinetHealthService.Accept(db,"TEST-CV",old,opsNow.AddSeconds(-150)),"Door-open fixture starts measured interval");await db.SaveChangesAsync();
    for(int i=1;i<=5;i++){var h=Health(boot:4,sequence:i+1,uptime:1000+i*30000);h.DoorOpen=true;h.MeasuredAtUtc=opsNow.AddSeconds(-150+i*30);await CabinetHealthService.Accept(db,"TEST-CV",h,opsNow.AddSeconds(-150+i*30));await db.SaveChangesAsync();}
    await OperationRules.Evaluate(db,config,opsNow);
    Check(await db.OperationAlerts.AnyAsync(a=>a.RoomId=="TEST-CV"&&a.Rule=="DOOR_OPEN_LONG"&&a.Status=="OPEN"),"Continuous fresh door-open measurements produce long-open alert");
    await OperationRules.Evaluate(db,config,opsNow);
    Check(await db.OperationAlerts.CountAsync(a=>a.RoomId=="TEST-CV"&&a.Rule=="DOOR_OPEN_LONG"&&a.Status=="OPEN")==1,"Repeated evaluations do not duplicate open alerts");
    await OperationRules.Evaluate(db,config,opsNow.AddSeconds(100));
    Check(await db.OperationAlerts.AnyAsync(a=>a.Rule=="CABINET_MESSAGES_MISSING"&&a.Status=="OPEN"),"Missing cabinet heartbeat persists an alert");
    Check(await db.OperationAlerts.AnyAsync(a=>a.Rule=="DOOR_OPEN_LONG"&&a.Status=="OPEN"),"Missing heartbeat does not falsely resolve open-door alert");
    var closed=Health(boot:4,sequence:7,uptime:300000);closed.MeasuredAtUtc=opsNow.AddSeconds(101);closed.DoorOpen=false;
    await CabinetHealthService.Accept(db,"TEST-CV",closed,opsNow.AddSeconds(101));await db.SaveChangesAsync();
    await OperationRules.Evaluate(db,config,opsNow.AddSeconds(101));
    Check(!await db.OperationAlerts.AnyAsync(a=>a.RoomId=="TEST-CV"&&a.Rule=="DOOR_OPEN_LONG"&&a.Status=="OPEN"),"Measured door closure resolves long-open alert and preserves history");
}
using(var http=new HttpClient{BaseAddress=new Uri(address)}) {
    http.DefaultRequestHeaders.Add("X-Test-User","OWNER");
    Check((await http.GetAsync("/api/admin/operations-telemetry/alerts")).StatusCode==System.Net.HttpStatusCode.Forbidden,"Operational alerts require admin role");
    http.DefaultRequestHeaders.Add("X-Test-Role","ADMIN");
    var registration=await http.PostAsJsonAsync("/api/admin/assets/nodes",new{nodeCode="FIXTURE-REGISTERED",roomId="TEST-CV",name="Fixture registered node",publishIntervalSeconds=30});
    Check(registration.IsSuccessStatusCode,"Admin registers real node without fabricating measurements or online status");
    var duplicateNode=await http.PostAsJsonAsync("/api/admin/assets/nodes",new{nodeCode="FIXTURE-REGISTERED",roomId="TEST-CV",name="Duplicate fixture",publishIntervalSeconds=30});
    Check(duplicateNode.StatusCode==System.Net.HttpStatusCode.Conflict,"Existing demo or real node identity cannot be overwritten");
    var sensorHistory=await http.GetFromJsonAsync<JsonElement>("/api/admin/assets/nodes/9001/history");
    Check(sensorHistory.GetProperty("dataSource").GetString()=="REAL"&&sensorHistory.GetProperty("samples").GetArrayLength()>0,"Sensor history retains source, clock quality and measurements");
    var history=await http.GetFromJsonAsync<JsonElement>("/api/admin/operations-telemetry/cabinets/TEST-CV/history");
    Check(history.GetProperty("source").GetString()=="REAL"&&history.GetProperty("items").GetArrayLength()>0,"Cabinet history API labels measured source explicitly");
    Check((await http.GetAsync("/api/admin/operations-telemetry/cabinets/TEST-CV/history?hours=10000")).StatusCode==System.Net.HttpStatusCode.BadRequest,"History time range is bounded");
}
var readyMqtt=new FakeMqtt{IsReady=false};
var readiness=new ReadinessCheck(app.Services.GetRequiredService<IServiceScopeFactory>(),readyMqtt);
Check((await readiness.CheckHealthAsync(new())).Status==Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy,"Readiness fails without MQTT subscription acknowledgment");
readyMqtt.IsReady=true;
Check((await readiness.CheckHealthAsync(new())).Status==Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy,"Readiness probes all required isolated schemas");
var logDirectory=Path.Combine(Path.GetTempPath(),"classhub-log-test-"+Guid.NewGuid().ToString("N"));
try {
    var logConfig=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["MqttSettings:Password"]="fixture-private-password"}).Build();
    using var logs=new RollingFileLogger(logDirectory,logConfig,maxBytes:1024,retained:3);
    var logger=logs.CreateLogger("Fixture");for(int i=0;i<20;i++)logger.LogInformation("fixture-private-password {Padding}",new string('x',400));
    var files=Directory.GetFiles(logDirectory);
    Check(files.Length<=3 && files.All(f=>!File.ReadAllText(f).Contains("fixture-private-password")),"File logs rotate and redact configured secrets");
} finally {foreach(var file in Directory.GetFiles(logDirectory))File.Delete(file);Directory.Delete(logDirectory);}


// The corridor retains its historical room ID while MQTT uses the short alias.
const string hallRoom="HALL-aaaaaaaaaaaaaaaaaaaa-6-1";
var hallMqtt=app.Services.GetRequiredService<FakeMqtt>();
await using(var db=Db()) {
 db.phong_hoc.Add(new PhongHoc{ma_phong=hallRoom,ten_phong="Fixture corridor",LocationType="CORRIDOR",tang=6});
 db.thiet_bi_iot.Add(new ThietBiIot{id=30,ma_phong=hallRoom,ten_tu="Fixture corridor cabinet",mac_address="00:00:00:00:00:30",mqtt_topic="classhub/cabinet/TST6",trang_thai_mang=true,trang_thai_khoa="LOCKED"});
 if(!await db.cau_hinh_ca_hoc.AnyAsync(c=>c.ca_id==1)) db.cau_hinh_ca_hoc.Add(new CauHinhCaHoc{ca_id=1,ten_ca="Fixture slot",gio_bat_dau=TimeSpan.FromHours(7),gio_ket_thuc=TimeSpan.FromHours(9)});
 db.phieu_muon.Add(new PhieuMuon{id=1401,ma_sv="OWNER",ma_phong=hallRoom,ca_muon=1,ngay_muon=DateTime.Today,trang_thai="PENDING"});
 await db.SaveChangesAsync();
 Check(await CabinetAddress.ForRoom(db,hallRoom)=="TST6" && await CabinetAddress.RoomForDevice(db,"TST6")==hallRoom,"Corridor maps short MQTT alias back to historical room");
 Check(await CabinetAddress.RoomForDevice(db,hallRoom)==null,"Long legacy corridor ID cannot impersonate the short device ID");
 var legacyRoom="STANDARD-aaaaaaaaaaaaaaaaaaaa-1-1";
 db.thiet_bi_iot.Add(new ThietBiIot{id=32,ma_phong=legacyRoom,ten_tu="Fixture standard cabinet",mac_address="00:00:00:00:00:32",mqtt_topic="classhub/cabinet/"+legacyRoom,trang_thai_mang=false});
 await db.SaveChangesAsync();
 Check(await CabinetAddress.ForRoom(db,legacyRoom)==legacyRoom && await CabinetAddress.RoomForDevice(db,legacyRoom)==legacyRoom,"Existing long STANDARD room routing is preserved pending separate migration");
 var service=new QrAccessService(db,hallMqtt,hub);
 await service.Issue(1401,"OWNER","RECEIVE");
 var issued=hallMqtt.Messages.Last();
 using var issuedJson=JsonDocument.Parse(issued.Payload);
 Check(issued.Topic=="backend/cabinet/TST6/qr" && issuedJson.RootElement.GetProperty("room").GetString()=="TST6","QR request topic and payload use the same short device ID");
 var token=issuedJson.RootElement.GetProperty("content").GetString()!["classhub:qr:v1:".Length..];
 await service.Preview(1401,"OWNER",token,"RECEIVE");
 Check(hallMqtt.Messages.Last().Topic=="backend/cabinet/TST6/action" && hallMqtt.Messages.Last().Payload.Contains("\"room\":\"TST6\""),"QR scan feedback uses short alias in topic and payload");
 await service.Open(1401,"OWNER",token,"RECEIVE");
 var command=await db.QrAccesses.SingleAsync(q=>q.SessionId==1401);
 Check(hallMqtt.Messages.Last().Topic=="backend/cabinet/TST6/action" && hallMqtt.Messages.Last().Payload.Contains("\"room\":\"TST6\""),"Open command uses the correct device alias");
 Check((await db.phieu_muon.FindAsync(1401))!.ma_phong==hallRoom,"Alias routing does not change session room identity");
 await service.Retry(1401,"OWNER");
 Check(hallMqtt.Messages.Last().Topic=="backend/cabinet/TST6/action" && hallMqtt.Messages.Last().Payload.Contains(command.CommandId!),"Retry retains command ID and short device topic");
}
await using(var db=Db()) {
 var controller=new AdminCabinetsController(db,hallMqtt,hub,app.Services.GetRequiredService<ILogger<AdminCabinetsController>>()){ControllerContext=UserContext("ADMIN",true)};
 Check(await controller.RemoteLockDoor(30) is AcceptedResult && hallMqtt.Messages.Last().Topic=="backend/cabinet/TST6/action" && hallMqtt.Messages.Last().Payload.Contains("\"room\":\"TST6\""),"Admin command routes to short alias");
}
var hallProcessor=new BackgroundServices(hallMqtt,app.Services.GetRequiredService<IServiceScopeFactory>(),hub,app.Services.GetRequiredService<ILogger<BackgroundServices>>());
var hallHealth=Health(boot:1,sequence:1,uptime:1000);hallHealth.MeasuredAtUtc=DateTime.UtcNow;
var validStatus=JsonSerializer.Serialize(new{room="TST6",isOnline=true,doorOpen=false,health=hallHealth},NodeTelemetry.Json);
await hallProcessor.ProcessMqttStatusMessageAsync("iot/cabinet/TST6/heartbeat",validStatus);
await using(var db=Db()) Check(await db.CabinetHealths.AnyAsync(h=>h.RoomId==hallRoom) && await db.OperationSamples.AnyAsync(h=>h.RoomId==hallRoom),"Short heartbeat persists health using the long historical room ID");
var spoofed=JsonSerializer.Serialize(new{room="INCIDENT-TEST",isOnline=true,lockState="UNLOCKED"},NodeTelemetry.Json);
await hallProcessor.ProcessMqttStatusMessageAsync("iot/cabinet/TST6/status",spoofed);
await using(var db=Db()) Check((await db.thiet_bi_iot.FindAsync(2))!.trang_thai_khoa!="UNLOCKED","Topic and payload room mismatch cannot update another cabinet");
string commandId;
await using(var db=Db()) commandId=(await db.QrAccesses.SingleAsync(q=>q.SessionId==1401)).CommandId!;
var eventPayload=JsonSerializer.Serialize(new{sessionId=1401,commandId,@event="STATE",revision=1,phase="UNLOCKED",doorOpened=false,rfidSequence=0,rfidUids=Array.Empty<string>()});
await hallProcessor.ProcessQrResult("iot/cabinet/TST6/qr-result",eventPayload);
await using(var db=Db()) Check((await db.QrAccesses.SingleAsync(q=>q.SessionId==1401)).DeviceRevision==1,"Short topic device receipt resolves to the correct session");
Check(hallMqtt.Messages.Last().Topic=="backend/cabinet/TST6/action" && hallMqtt.Messages.Last().Payload.Contains("\"room\":\"TST6\""),"Receipt ACK uses the short device ID");
await using(var db=Db()) {
 db.thiet_bi_iot.Add(new ThietBiIot{id=31,ma_phong="TST6",ten_tu="Colliding cabinet",mac_address="00:00:00:00:00:31",mqtt_topic="other/TST6",trang_thai_mang=true});
 await db.SaveChangesAsync();
 Check(await CabinetAddress.RoomForDevice(db,"TST6")==null,"Alias collision is rejected instead of selecting a wrong cabinet");
 try {await CabinetAddress.ForRoom(db,hallRoom);throw new Exception("Collision accepted");} catch(InvalidOperationException){Check(true,"Outbound commands fail closed on ambiguous alias");}
}

// A previously stored password must not force login for the newly open console.
config["ReturnVerification:Cameras:TEST-CV:AdminPassword"] = "FIXTURE-stale-admin-password";
config["ReturnVerification:Cameras:TEST-CV:RequireAuthentication"] = "false";
config["ReturnVerification:Cameras:TEST-CV:WebSocketUrl"] = address.Replace("http:", "ws:") + "/ws";
var openCamera = new CabinetCamera(config);
var openPhoto = await openCamera.Capture("TEST-CV", CancellationToken.None);
Check(openPhoto.Jpeg.Length == 6 && cameraLogins == 0, "Open camera capture ignores a previously stored admin password");

requireCameraAuth = true;
config["ReturnVerification:Cameras:TEST-CV:RequireAuthentication"] = "true";
config["ReturnVerification:Cameras:TEST-CV:AdminPassword"] = "FIXTURE-admin-password-123";
config["ReturnVerification:Cameras:TEST-CV:WebSocketUrl"] = address.Replace("http:", "ws:") + "/ws";
var authenticatedCamera = new CabinetCamera(config);
var securePhoto = await authenticatedCamera.Capture("TEST-CV", CancellationToken.None);
Check(securePhoto.Jpeg.Length == 6 && cameraLogins == 1, "Backend authenticates camera and sends token before frame commands");
await authenticatedCamera.Capture("TEST-CV", CancellationToken.None);
Check(cameraLogins == 1, "Backend reuses its bounded camera session without repeated login");
await app.StopAsync();

Console.WriteLine($"{checks} return verification checks passed; no production sessions or hardware changed.");

class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (string.IsNullOrWhiteSpace(user)) return Task.FromResult(AuthenticateResult.NoResult());
        var role = Request.Headers["X-Test-Role"].ToString();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user),
            new Claim(ClaimTypes.Role, role == "ADMIN" ? "ADMIN" : "USER")], "Test"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
    }
}

class FakeMqtt : ClassHub_API.Services.IMqttService
{
    public List<(string Topic, string Payload)> Messages { get; } = [];
    public bool FailPublish { get; set; }
    public bool IsReady { get; set; } = true;
    public Task PublishAsync(string topic, string payload) {
        if (FailPublish) return Task.FromException(new InvalidOperationException("Simulated broker outage"));
        Messages.Add((topic, payload)); return Task.CompletedTask;
    }
    public Task SubscribeAsync(string topic, Action<string, string> onMessageReceived) => Task.CompletedTask;
}
