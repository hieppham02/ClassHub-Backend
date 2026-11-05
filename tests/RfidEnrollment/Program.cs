using ClassHub_API.Services;
using Microsoft.Extensions.Configuration;
using System.Buffers.Binary;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;
using System.Text;
int checks=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS: "+name);checks++;}
var now=DateTime.UtcNow;
var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {["JwtSettings:SecretKey"]=new string('x',48)}).Build();
var tokens=new RfidEnrollmentTokens(config);
var p=new CabinetHealthPayload {SchemaVersion=1,Source="REAL",BootId=new string('a',32),BootSequence=1,Sequence=1,
    FirmwareId="test",FirmwareVersion="1",ClockSynced=true,MeasuredAtUtc=now,LastRfidScanAgeMs=1000,
    LastRfidUid="E3940127",RfidScanSequence=1};
var health=new CabinetHealthView(now,0,false,"CLOCK_SYNCED",p);
Check(CabinetHealthService.Valid(p),"New RFID telemetry is valid");
var scan=tokens.Issue(1,"ROOM1",health,now)!;
Check(scan.Uid=="E3940127" && scan.ScannedAtUtc==now.AddSeconds(-1),"Scan time derives from measurement time, not receipt");
Check(tokens.Read(scan.SelectionToken,1,"ROOM1",now)?.Uid=="E3940127","Selection accepted for correct cabinet");
Check(tokens.Read(scan.SelectionToken,2,"ROOM1",now)==null,"Selection cannot move to another cabinet");
Check(tokens.Read(scan.SelectionToken,1,"ROOM2",now)==null,"Selection cannot move to another room");
Check(tokens.Read(scan.SelectionToken,1,"ROOM1",now.AddMinutes(3))==null,"Expired selection rejected");
var parts=scan.SelectionToken.Split('.');
Check(tokens.Read(parts[0]+"."+new string('A',parts[1].Length),1,"ROOM1",now)==null,"Tampered selection rejected");
p.LastRfidUid="11223344";p.RfidScanSequence=2;
Check(tokens.Read(scan.SelectionToken,1,"ROOM1",now)?.Uid=="E3940127","Another scan cannot change explicitly selected card");
Check(tokens.Issue(1,"ROOM1",health with {AgeSeconds=46},now)==null,"Stale health cannot issue selection");
Check(tokens.Issue(1,"ROOM1",health with {Quality="UNSYNCED"},now)==null,"Unsynchronized timestamps cannot issue selection");
p.MeasuredAtUtc=now.AddMinutes(-4);
Check(tokens.Issue(1,"ROOM1",health,now)==null,"Retained MQTT payload cannot make old scan fresh");
p.MeasuredAtUtc=now.AddSeconds(10);
Check(tokens.Issue(1,"ROOM1",health,now)==null,"Future scan cannot issue selection");
p.LastRfidUid=null;p.RfidScanSequence=null;
Check(CabinetHealthService.Valid(p),"Older firmware telemetry remains compatible");
p.RfidScanSequence=0;
Check(CabinetHealthService.Valid(p),"New firmware with no scans remains compatible");
p.LastRfidUid="not-a-uid";p.RfidScanSequence=1;
Check(!CabinetHealthService.Valid(p),"Malformed UID rejected");
var packet=new byte[20];"OVF1"u8.CopyTo(packet);BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4),640);
BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6),480);packet[14]=1;packet[16]=255;packet[17]=216;packet[18]=255;packet[19]=217;
Check(CabinetCamera.Decode(packet).Jpeg.Length==4,"Camera native VGA packet decoded");
packet[14]=0;
try {CabinetCamera.Decode(packet);throw new Exception("Raw frame accepted");}
catch(InvalidDataException){checks++;Console.WriteLine("PASS: raw frames cannot enter JPEG stream");}
// A local WebSocket fixture verifies persistent NEXT/backpressure and single-viewer cleanup.
var builder=WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
var app=builder.Build();app.UseWebSockets();
app.Map("/ws",async context=>{
    using var socket=await context.WebSockets.AcceptWebSocketAsync();
    var input=new byte[100];var fixture=new byte[20];"OVF1"u8.CopyTo(fixture);
    BinaryPrimitives.WriteUInt16LittleEndian(fixture.AsSpan(4),640);BinaryPrimitives.WriteUInt16LittleEndian(fixture.AsSpan(6),480);
    fixture[14]=1;fixture[16]=255;fixture[17]=216;fixture[18]=255;fixture[19]=217;
    uint sequence=0;
    try {
        while(socket.State==WebSocketState.Open) {
            var msg=await socket.ReceiveAsync(new ArraySegment<byte>(input),context.RequestAborted);
            if(msg.MessageType==WebSocketMessageType.Close) {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"closed",CancellationToken.None);break;
            }
            if(Encoding.UTF8.GetString(input,0,msg.Count)!="NEXT")continue;
            BinaryPrimitives.WriteUInt32LittleEndian(fixture.AsSpan(8),++sequence);
            await socket.SendAsync(new ArraySegment<byte>(fixture,0,9),WebSocketMessageType.Binary,false,context.RequestAborted);
            await socket.SendAsync(new ArraySegment<byte>(fixture,9,11),WebSocketMessageType.Binary,true,context.RequestAborted);
        }
    }catch(Exception ex)when(ex is WebSocketException or OperationCanceledException){}
});
await app.StartAsync();
try {
    var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    var cameraConfig=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
        ["ReturnVerification:Cameras:TEST:WebSocketUrl"]=address.Replace("http://","ws://")+"/ws"}).Build();
    var camera=new CabinetCamera(cameraConfig);
    using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await using(var stream=camera.Stream("TEST",timeout.Token).GetAsyncEnumerator()) {
        Check(await stream.MoveNextAsync() && stream.Current.Sequence==1,"Persistent camera gets first fragmented frame");
        Check(await stream.MoveNextAsync() && stream.Current.Sequence==2,"Persistent camera requests next fresh frame");
        try {await camera.Capture("TEST",timeout.Token);throw new Exception("Second viewer accepted");}
        catch(InvalidDataException){checks++;Console.WriteLine("PASS: capture cannot interrupt active viewer");}
    }
    Check((await camera.Capture("TEST",timeout.Token)).Sequence==1,"Closing viewer releases camera for return verification");
}finally {await app.StopAsync();await app.DisposeAsync();}
Console.WriteLine($"PASS: {checks} enrollment and camera checks; no database opened");
