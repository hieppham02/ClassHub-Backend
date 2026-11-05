using ClassHub_API.Services;
using ClassHub_API.Controllers;
using ClassHub_API.Data;
using ClassHub_API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
int count=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);Console.WriteLine("PASS: "+name);count++;}
var now=DateTime.UtcNow;
var body=new NodeMessage{SchemaVersion=1,Source="MOCK",NodeCode="DEMO_ROOM1",RoomId="ROOM1",BootId=Guid.NewGuid().ToString(),
 BootSequence=1,Sequence=1,ClockSynced=true,MeasuredAtUtc=now,Readings=[new("temperature","C",41,"GOOD"),new("humidity","%",65,"GOOD"),new("current","A",1,"GOOD")]};
Check(NodeTelemetry.Valid(body,now),"Canonical MQTT simulator payload accepted");
Check(NodeTelemetry.SourceMatches("MOCK","MOCK"),"Demo stream maps only to registered MOCK node");
Check(!NodeTelemetry.SourceMatches("REAL","MOCK"),"Simulator cannot feed a physical node");
Check(!NodeTelemetry.SourceMatches("MOCK","REAL"),"REAL label cannot convert mock data");
Check(!NodeTelemetry.SourceMatches("MOCK","OTHER"),"Unsupported source rejected");
body.Source="REAL";Check(NodeTelemetry.Valid(body,now),"Existing physical telemetry unchanged");body.Source="OTHER";Check(!NodeTelemetry.Valid(body,now),"Unknown source rejected");body.Source="MOCK";
body.Sequence=0;Check(!NodeTelemetry.Valid(body,now),"Zero sequence rejected");body.Sequence=1;
body.MeasuredAtUtc=now.AddMinutes(1);Check(!NodeTelemetry.Valid(body,now),"Future simulated timestamp rejected");body.MeasuredAtUtc=now;
body.Readings=[new("current","W",20,"GOOD")];Check(!NodeTelemetry.Valid(body,now),"Tool must publish amperes, not watts");
Check(NodeTelemetry.Current(new IotSensor{DataSource="MOCK",ClockSynced=true,MeasuredAt=now,ReadingsJson="[]"})==null,"Test readings do not become real operational-current evidence");
using var db=new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().Options);
var controller=new AssetsController(db,new ConfigurationBuilder().Build());
Check(await controller.RegisterTestingNode(new("PHYSICAL_NODE","ROOM1","Test",10)) is BadRequestObjectResult,"Testing registration requires DEMO_ prefix before DB access");
Check(typeof(AssetsController).GetCustomAttributes(typeof(AuthorizeAttribute),false).Cast<AuthorizeAttribute>().Single().Roles=="ADMIN","Node registration is admin-only");
Console.WriteLine($"PASS: {count} backend test-node checks; no database opened");
