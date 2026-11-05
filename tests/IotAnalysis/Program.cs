using ClassHub_API.Services;
using ClassHub_API.Controllers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
var path=Path.GetFullPath("ClassHub-API/Models/iot_forest.json");
var model=new IotAnomalyModel(path);
using var doc=JsonDocument.Parse(File.ReadAllText(path));
int count=0;
void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
foreach(var vector in doc.RootElement.GetProperty("goldenVectors").EnumerateArray()){
    var f=vector.GetProperty("input").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
    var actual=model.Predict(f[0],f[1],f[2],f[3]==1);
    var expected=vector.GetProperty("probabilities").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
    Check(actual.Zip(expected).All(pair=>Math.Abs(pair.First-pair.Second)<1e-10),"Python/.NET probabilities agree: "+string.Join(",",f));
}
var idle=model.Predict(28,65,.08,false);
Check(idle[0]>.6,"Idle room is normal");
foreach(var amps in new[]{.985,1.59,1.89,12.2}){
    var scores=model.Predict(28,65,amps,false);
    Check(scores[1]+scores[3]>=.6,"Residual load flagged at "+amps+" A");
}
var occupied=model.Predict(25,57,26,true);
Check(occupied[1]+occupied[3]<.6,"High current during active use is not forgotten load");
var unknown=model.Predict(28,65,12,true);
Check(unknown[1]+unknown[3]<.6,"Conservative inference does not flag forgotten load without use context");
var heat=model.Predict(41,48,.08,false);
Check(heat[2]+heat[3]>.6,"High temperature flagged independently of current");
try{model.Predict(double.NaN,60,1,false);throw new Exception("NaN accepted");}catch(ArgumentException){count++;}
var payload="""{"schemaVersion":1,"source":"MOCK","demo":{"context":"AFTER_USE","scenario":"forgot_tv","loads":{"tv":1}},"readings":[{"metric":"current","unit":"A","value":1,"quality":"GOOD","usageContext":"IN_USE"}]}""";
var parsed=JsonSerializer.Deserialize<NodeMessage>(payload,NodeTelemetry.Json)!;
Check(parsed.Demo!.Context=="AFTER_USE","Only context is consumed from demo metadata");
var restored=JsonSerializer.Deserialize<List<SensorReading>>(JsonSerializer.Serialize(new[]{new SensorReading("current","A",1,"GOOD","AFTER_USE")},NodeTelemetry.Json),NodeTelemetry.Json)!;
Check(restored[0].UsageContext=="AFTER_USE","Usage context survives stored JSON roundtrip");
Check(typeof(IotAnalysisController).GetCustomAttributes(typeof(AuthorizeAttribute),false).Cast<AuthorizeAttribute>().Single().Roles=="ADMIN","Analysis is admin-only");
string Data(double temp,double humidity,double amperes,string quality="GOOD",string? usage="AFTER_USE")=>JsonSerializer.Serialize(new[]{new SensorReading("temperature","C",temp,quality),new SensorReading("humidity","%",humidity,quality),new SensorReading("current","A",amperes,quality,usage)},NodeTelemetry.Json);
Check(IotAttention.Check(Data(36,60,.08),"REAL",model),"Overview flags high temperature");
Check(IotAttention.Check(Data(28,80,.08),"REAL",model),"Overview flags high humidity");
Check(IotAttention.Check(Data(28,60,12),"MOCK",model),"Overview flags AI residual load");
Check(!IotAttention.Check(Data(28,60,12,usage:"IN_USE"),"MOCK",model),"Occupied room is excluded from residual-load warning");
Check(!IotAttention.Check(Data(28,60,12),"REAL",model),"Physical nodes cannot take usage context from payload");
Check(!IotAttention.Check(Data(41,90,12,"ERROR"),"MOCK",model),"Bad-quality readings cannot flag room");
Check(!IotAttention.Check("{broken","MOCK",model),"Malformed JSON is safe");
Check(!IotAttention.Check(null,"MOCK",model),"Missing telemetry is safe");
Console.WriteLine($"PASS {count} checks; no DB connection or MQTT publish.");
