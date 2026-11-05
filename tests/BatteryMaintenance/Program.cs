using ClassHub_API.Controllers;
using ClassHub_API.Data;
using ClassHub_API.DTOs;
using ClassHub_API.Models;
using ClassHub_API.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;

int checks=0;
void Check(bool value,string name) {if(!value)throw new Exception("FAIL: "+name);checks++;Console.WriteLine("PASS: "+name);}
var now=new DateTime(2026,11,5,12,0,0,DateTimeKind.Utc);
var p=new EquipmentBatteryProfile {Id=1,EquipmentId=1,AssetCode="TEST-MIC"};
EquipmentBatteryInspection Inspect(string battery="UNKNOWN",string operation="UNKNOWN",double age=1)=>new() {Id=1,ProfileId=1,DataSource="REAL",ObservedAtUtc=now.AddHours(-age),BatteryState=battery,OperationState=operation};
EquipmentBatteryService Service(string action="REPLACE_BATTERY",string outcome="NORMAL",double age=0.5,bool? full=null)=>new() {Id=1,ProfileId=1,DataSource="REAL",Action=action,Outcome=outcome,PerformedAtUtc=now.AddHours(-age),RechargeCompleted=full};
BatteryDecision Eval(EquipmentBatteryInspection? b=null,EquipmentBatteryInspection? o=null,EquipmentBatteryService? s=null,EquipmentBatteryService? c=null,double? expected=null,double fresh=168)=>
    BatteryRules.Evaluate(p,b,o,s,c,now,fresh,expected);
Check(Eval().Priority=="UNKNOWN","No evidence does not imply a healthy microphone");
Check(Eval(Inspect("LOW")).Priority=="HIGH","Known weak battery requires priority inspection");
Check(Eval(Inspect("EMPTY",age:900)).Priority=="HIGH","Old unresolved empty battery is not silently cleared by age");
Check(Eval(o:Inspect(operation:"INTERMITTENT")).Priority=="HIGH","Intermittent operation requires inspection without asserting battery cause");
Check(Eval(o:Inspect(operation:"NOT_WORKING")).Priority=="HIGH","Non-working microphone has high priority");
var good=Inspect("GOOD","NORMAL");
Check(Eval(good,good).Priority=="LOW","Recent explicit battery and operation tests allow low priority");
Check(Eval(good).Priority=="UNKNOWN","Good battery alone does not prove microphone operation");
Check(Eval(o:good).Priority=="UNKNOWN","Working microphone alone does not prove battery health");
var stale=Inspect("GOOD","NORMAL",168);
Check(Eval(stale,stale).Priority=="UNKNOWN","Healthy evidence expires exactly at configured freshness threshold");
Check(Eval(good,good).ValidUntilUtc==good.ObservedAtUtc.AddHours(168),"Next check time derives from evidence not page load");
var replaced=Service();
Check(Eval(Inspect("LOW"),Inspect(operation:"NOT_WORKING"),replaced,replaced).Priority=="LOW","Confirmed normal replacement resolves older faults");
Check(Eval(Inspect("LOW",age:0.1),good,replaced,replaced).Priority=="HIGH","A new weak battery report after replacement is still high priority");
var partial=Service("RECHARGE",full:false);
Check(Eval(Inspect("LOW"),good,partial,partial).Priority=="HIGH","Partial recharge cannot reset battery cycle or clear low battery");
var full=Service("RECHARGE",full:true);
Check(Eval(Inspect("LOW"),good,full,full).Priority=="LOW","Confirmed normal full recharge starts a new cycle");
var unknown=Service(outcome:"UNKNOWN");
Check(Eval(Inspect("LOW"),good,unknown,unknown).Priority=="HIGH","Unconfirmed replacement does not claim successful repair");
var repair=Service("REPAIR");
Check(Eval(Inspect("LOW"),Inspect(operation:"NOT_WORKING"),repair).Priority=="HIGH","Repair of operation does not erase a known battery problem");
var failed=Service(outcome:"STILL_FAULTY");
Check(Eval(good,good,failed).Priority=="HIGH","Unsuccessful service remains high priority");
Check(Eval(good,Inspect(operation:"NORMAL",age:0.1),failed).Priority=="LOW","Later normal operation test can resolve a previous operation fault");
var aged=Service(age:49);
Check(Eval(s:aged,c:aged,expected:48).Priority=="MEDIUM","Only configured cycle age produces a planned inspection");
Check(Eval(s:aged,c:aged).Priority=="LOW","No invented universal battery lifetime");
Check(Eval(Inspect("LOW",age:0.1),good,aged,aged,48).Priority=="HIGH","Observed faults take precedence over age-based reminders");
Check(Eval(good,good,fresh:double.NaN).Priority=="LOW","Invalid freshness setting falls back to documented duration");
var mock=Inspect("EMPTY");mock.DataSource="MOCK";
Check(Eval(mock).Priority=="UNKNOWN","Mock inspection cannot drive real maintenance queue");
var future=Inspect("EMPTY",age:-1);
Check(Eval(future).Priority=="UNKNOWN","Future evidence excluded");
var same=Service(age:1);
Check(Eval(Inspect("LOW"),good,same,same).Priority=="LOW","Service wins same-time inspection deterministically");
p.Enabled=false;Check(Eval(Inspect("LOW")).Priority=="UNKNOWN","Disabled profiles excluded from actionable rules");p.Enabled=true;
bool Valid(object obj)=>Validator.TryValidateObject(obj,new ValidationContext(obj),new List<ValidationResult>(),true);
Check(!Valid(new BatteryProfileRequest {EquipmentId=1,AssetCode="bad code"}),"Asset identifiers reject whitespace and punctuation");
Check(!Valid(new BatteryProfileRequest {EquipmentId=1,AssetCode="MIC-1",BatteryCount=0}),"Zero battery count rejected");
Check(!Valid(new BatteryInspectionRequest {Version=1,BatteryPercent=101}),"Percent over 100 rejected");
Check(!Valid(new BatteryInspectionRequest {Version=1,OperationState="GOOD"}),"Unsupported operation state rejected");
Check(!Valid(new BatteryInspectionRequest {Version=0}),"Missing profile version cannot mutate evidence");
Check(!Valid(new BatteryServiceRequest {Version=1,Action="MAGIC"}),"Unknown service action rejected");

var configuration=new ConfigurationBuilder().Build();
var builder=WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
var connection=Environment.GetEnvironmentVariable("CLASSHUB_BATTERY_READONLY_CONNECTION");
builder.Services.AddSingleton<IConfiguration>(configuration);
builder.Services.AddDbContext<AppDbContext>(o=>o.UseMySql(connection ?? "Server=127.0.0.1;Database=unused;User Id=unused;",new MariaDbServerVersion(new Version(10,4,32))));
builder.Services.AddScoped<BatteryAssessmentService>();
builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions,RoleAuth>("Test",_=>{});
builder.Services.AddAuthorization();
builder.Services.AddControllers().AddApplicationPart(typeof(EquipmentBatteryController).Assembly);
await using var app=builder.Build();
app.UseAuthentication();app.UseAuthorization();app.MapControllers();await app.StartAsync();
var url=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var client=new HttpClient {BaseAddress=new Uri(url)};
foreach(var path in new[]{"","/options","/1","/1/inspections","/1/services"}) {
    var response=await client.GetAsync("/api/admin/batteries"+path);
    Check(response.StatusCode==HttpStatusCode.Unauthorized,"Anonymous access rejected: "+path);
}
client.DefaultRequestHeaders.Add("X-Test-Role","STUDENT");
Check((await client.GetAsync("/api/admin/batteries")).StatusCode==HttpStatusCode.Forbidden,"Student cannot view maintenance data");
client.DefaultRequestHeaders.Remove("X-Test-Role");client.DefaultRequestHeaders.Add("X-Test-Role","ADMIN");
Check((await client.PostAsJsonAsync("/api/admin/batteries",new {equipmentId=0,assetCode=""})).StatusCode==HttpStatusCode.BadRequest,"HTTP validation rejects invalid profile before database access");
Check((await client.PostAsJsonAsync("/api/admin/batteries/1/inspections",new {version=0,batteryPercent=150})).StatusCode==HttpStatusCode.BadRequest,"HTTP validation rejects invalid measurements");

if(connection!=null) {
    // Only GET endpoints and SELECT queries. No schema creation, no DML, no running app workers.
    Check((await client.GetAsync("/api/admin/batteries/options")).IsSuccessStatusCode,"Options query reads existing schema");
    Check((await client.GetAsync("/api/admin/batteries")).IsSuccessStatusCode,"Maintenance list reads existing empty schema");
    using var scope=app.Services.CreateScope();
    var db=scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var svc=scope.ServiceProvider.GetRequiredService<BatteryAssessmentService>();
    var result=await svc.Evaluate([new EquipmentBatteryProfile {Id=long.MaxValue,AssetCode="NONEXISTENT"}],DateTime.UtcNow);
    Check(result[long.MaxValue].Decision.Priority=="UNKNOWN","All four latest-evidence SQL queries execute with no matching profile");
    foreach(var t in new[]{typeof(EquipmentBatteryProfile),typeof(EquipmentBatteryInspection),typeof(EquipmentBatteryService),typeof(EquipmentBatteryAssessment)})
        Check(db.Model.FindEntityType(t)!=null,"EF model registered: "+t.Name);
}
await app.StopAsync();
Console.WriteLine("PASS: "+checks+" battery checks; live DB checks use SELECT/GET only.");

public class RoleAuth(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder)
    :AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder) {
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() {
        var role=Request.Headers["X-Test-Role"].ToString();
        if(string.IsNullOrEmpty(role))return Task.FromResult(AuthenticateResult.NoResult());
        var identity=new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,"TEST-NO-WRITES"),new Claim(ClaimTypes.Role,role)],"Test");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity),"Test")));
    }
}
