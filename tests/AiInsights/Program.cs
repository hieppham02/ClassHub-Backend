using ClassHub_API.Services;
using ClassHub_API.Controllers;
using ClassHub_API.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Net.Http.Json;
int checks=0;
void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);checks++;Console.WriteLine("PASS: "+name);}
var model=new BatteryForecastModel();var now=new DateTime(2026,11,6,0,0,0,DateTimeKind.Utc);
Check(model.Metadata.GetProperty("trainingSource").GetString()=="SYNTHETIC","Training provenance retained");
Check(model.Metadata.GetProperty("sampleCount").GetInt32()>1000,"A trained model artifact is shipped");
Check(typeof(AiInsightsController).GetCustomAttributes(typeof(AuthorizeAttribute),false).Cast<AuthorizeAttribute>().Single().Roles=="ADMIN","AI endpoints are admin-only");
foreach(var vector in model.GoldenVectors.EnumerateArray()){
    var f=vector.GetProperty("features").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
    var input=new BatteryForecastInput((int)f[0],f[1],f[2],f[3],f[4],f[5],f[6]==1);
    var result=model.Predict(input,now);
    Check(Math.Abs(result.RemainingDays-vector.GetProperty("expected").GetDouble())<.006,".NET inference matches sklearn export");
    Check(result.LowerDays<=result.RemainingDays && result.UpperDays>=result.RemainingDays,"Calibrated interval surrounds prediction");
}
var normal=new BatteryForecastInput(0,7,12,16,8,5,false);
Check(model.Predict(normal with {BorrowHoursTotal=28,BorrowEventsTotal=35,HoursLast7=20,ActiveDaysLast7=7},now).RemainingDays<model.Predict(normal,now).RemainingDays,"Demo responds to heavier usage rather than returning a fixed day");
Check(!(normal with {AgeDays=0}).Valid,"Impossible cumulative use rejected");
Check(!(normal with {BorrowHoursTotal=double.NaN}).Valid,"Nonfinite input rejected");
Check(!(normal with {ActiveDaysLast7=8}).Valid,"Seven-day active count bounded");
Check(!(normal with {HoursLast7=15}).Valid,"Recent hours cannot exceed cumulative hours");
Check(!(normal with {DeviceKind=2}).Valid,"Unsupported device kind rejected");
var day=new DateTime(2026,11,5);
var slots=Enumerable.Range(0,5).Select(i=>new UsageSlot(i+1,"Slot",TimeSpan.FromHours(8+i),TimeSpan.FromHours(9+i))).ToArray();
var windows=UsageIntervals.Windows(day,slots);
UsageInterval[] duplicate=[new(day.AddHours(8),day.AddHours(8.4)),new(day.AddHours(8.2),day.AddHours(8.8)),new(day.AddHours(8.7),day.AddHours(9))];
Check(Math.Abs(UsageIntervals.Hours(duplicate)-1)<1e-9,"Overlapping borrowing time not counted twice");
Check(UsageIntervals.UsedSlots(duplicate,windows)==1,"Three borrow events in one slot count as one used slot");
Check(UsageIntervals.UsedSlots([new(day,day.AddDays(1))],windows)==5,"A room cannot exceed five used slots per day");
Check(UsageIntervals.UsedSlots([new(day.AddHours(7),day.AddHours(8))],windows)==0,"Touching boundary is not occupancy");
Check(UsageIntervals.Overlap([new(day.AddHours(7),day.AddHours(10))],windows)==2,"Time utilization counts only available slot windows");
var overnight=UsageIntervals.Windows(day,[new(1,"Night",TimeSpan.FromHours(23),TimeSpan.FromHours(1))]);
Check(UsageIntervals.Hours(overnight)==2,"Overnight slots handled");
Check(UsageIntervals.Hours([new(day.AddHours(9),day.AddHours(8))])==0,"Invalid reversed intervals excluded");
var connection=Environment.GetEnvironmentVariable("CLASSHUB_AI_READONLY_CONNECTION");
if(connection!=null) {
    using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySql(connection,new MariaDbServerVersion(new Version(10,4,32))).Options);
    var controller=new AiInsightsController(db,model);
    var result=await controller.Rooms(null,null,CancellationToken.None) as OkObjectResult;
    Check(result!=null,"Room statistics query executes against existing database");
    var json=JsonSerializer.SerializeToElement(result!.Value);
    var all=json.GetProperty("items").EnumerateArray().ToArray();
    Check(all.All(r=>r.GetProperty("daily").EnumerateArray().All(d=>d.GetProperty("usedSlots").GetInt32()<=5)),"Every actual room/day stays within five slots");
    Check(all.All(r=>r.GetProperty("timeUtilizationPercent").GetDouble()<=100.01),"Actual time utilization never exceeds capacity");
    Check(await controller.Forecasts(CancellationToken.None) is OkObjectResult,"Existing battery data can be read without inserts");
    Console.WriteLine("READONLY_SUMMARY "+JsonSerializer.Serialize(new {rooms=all.Length,borrows=all.Sum(r=>r.GetProperty("borrowCount").GetInt32()),usedSlots=all.Sum(r=>r.GetProperty("usedSlots").GetInt32()),from=json.GetProperty("from").GetString(),to=json.GetProperty("to").GetString()}));
}
var builder=WebApplication.CreateBuilder();
builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddControllers().AddApplicationPart(typeof(AiInsightsController).Assembly);
builder.Services.AddSingleton(model);
builder.Services.AddDbContext<AppDbContext>(options=>{if(connection!=null)options.UseMySql(connection,new MariaDbServerVersion(new Version(10,4,32)));});
var key=new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('x',48)));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options=>options.TokenValidationParameters=new TokenValidationParameters {
    ValidateIssuer=false,ValidateAudience=false,ValidateIssuerSigningKey=true,IssuerSigningKey=key,ValidateLifetime=true});
builder.Services.AddAuthorization();
var app=builder.Build();app.UseAuthentication();app.UseAuthorization();app.MapControllers();
await app.StartAsync();
try {
    using var http=new HttpClient {BaseAddress=new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())};
    Check((int)(await http.GetAsync("/api/admin/ai/model")).StatusCode==401,"HTTP model endpoint rejects anonymous access");
    string Token(string role)=>new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims:[new Claim(ClaimTypes.NameIdentifier,"test"),new Claim(ClaimTypes.Role,role)],
        expires:DateTime.UtcNow.AddMinutes(5),signingCredentials:new SigningCredentials(key,SecurityAlgorithms.HmacSha256)));
    http.DefaultRequestHeaders.Authorization=new("Bearer",Token("STUDENT"));
    Check((int)(await http.GetAsync("/api/admin/ai/model")).StatusCode==403,"HTTP model endpoint rejects non-admin");
    http.DefaultRequestHeaders.Authorization=new("Bearer",Token("ADMIN"));
    Check((await http.GetAsync("/api/admin/ai/model")).IsSuccessStatusCode,"HTTP admin can load model metadata");
    var response=await http.PostAsJsonAsync("/api/admin/ai/battery/predict",normal);
    Check(response.IsSuccessStatusCode,"HTTP model runs inference through DI and controller binding");
    var body=await response.Content.ReadFromJsonAsync<JsonElement>();
    Check(Math.Abs(body.GetProperty("remainingDays").GetDouble()-model.Predict(normal,now).RemainingDays)<.001,"HTTP inference matches model result");
    Check((int)(await http.PostAsJsonAsync("/api/admin/ai/battery/predict",normal with {AgeDays=-1})).StatusCode==400,"HTTP invalid features rejected");
    if(connection!=null)Check((await http.GetAsync("/api/admin/ai/rooms")).IsSuccessStatusCode,"HTTP room analytics uses existing DB read-only");
}finally{await app.StopAsync();await app.DisposeAsync();}
Console.WriteLine($"PASS: {checks} AI/usage checks; no database writes");
