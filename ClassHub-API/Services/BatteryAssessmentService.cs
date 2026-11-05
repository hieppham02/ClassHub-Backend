using ClassHub_API.Data;
using ClassHub_API.Models;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.Json;
namespace ClassHub_API.Services;

public record BatteryEvaluation(BatteryDecision Decision, string EvidenceJson);
public class BatteryAssessmentService(AppDbContext db, IConfiguration config) {
    public double FreshHours => Positive("BatteryMaintenance:InspectionFreshHours") is double h && h is >= 1 and <= 8760 ? h : 168;
    double? Positive(string key) => double.TryParse(config[key],NumberStyles.Float,CultureInfo.InvariantCulture,out var x) && double.IsFinite(x) && x > 0 && x <= 87600 ? x : null;
    public async Task<Dictionary<long,BatteryEvaluation>> Evaluate(IReadOnlyList<EquipmentBatteryProfile> profiles,DateTime now) {
        if (profiles.Count == 0) return [];
        var ids=profiles.Select(x=>x.Id).ToArray();
        var inspections=db.BatteryInspections.AsNoTracking().Where(x=>ids.Contains(x.ProfileId) && x.DataSource=="REAL" && x.ObservedAtUtc<=now);
        var batteryQuery=inspections.Where(x=>x.BatteryState!="UNKNOWN");
        var batteries=await batteryQuery.Where(x=>x.Id==batteryQuery.Where(y=>y.ProfileId==x.ProfileId)
            .OrderByDescending(y=>y.ObservedAtUtc).ThenByDescending(y=>y.Id).Select(y=>y.Id).First()).ToDictionaryAsync(x=>x.ProfileId);
        var operationQuery=inspections.Where(x=>x.OperationState!="UNKNOWN");
        var operations=await operationQuery.Where(x=>x.Id==operationQuery.Where(y=>y.ProfileId==x.ProfileId)
            .OrderByDescending(y=>y.ObservedAtUtc).ThenByDescending(y=>y.Id).Select(y=>y.Id).First()).ToDictionaryAsync(x=>x.ProfileId);
        var services=db.BatteryServices.AsNoTracking().Where(x=>ids.Contains(x.ProfileId) && x.DataSource=="REAL" && x.PerformedAtUtc<=now && x.Outcome!="UNKNOWN");
        var outcomes=await services.Where(x=>x.Id==services.Where(y=>y.ProfileId==x.ProfileId)
            .OrderByDescending(y=>y.PerformedAtUtc).ThenByDescending(y=>y.Id).Select(y=>y.Id).First()).ToDictionaryAsync(x=>x.ProfileId);
        var cycles=services.Where(x=>x.Outcome=="NORMAL" && (x.Action=="REPLACE_BATTERY" || x.Action=="RECHARGE" && x.RechargeCompleted==true));
        var cycleRows=await cycles.Where(x=>x.Id==cycles.Where(y=>y.ProfileId==x.ProfileId)
            .OrderByDescending(y=>y.PerformedAtUtc).ThenByDescending(y=>y.Id).Select(y=>y.Id).First()).ToDictionaryAsync(x=>x.ProfileId);
        return profiles.ToDictionary(p=>p.Id,p=>{
            batteries.TryGetValue(p.Id,out var battery); operations.TryGetValue(p.Id,out var operation);
            outcomes.TryGetValue(p.Id,out var service); cycleRows.TryGetValue(p.Id,out var cycle);
            var expected=Positive("BatteryMaintenance:ExpectedCycleHours:"+p.AssetCode);
            var decision=BatteryRules.Evaluate(p,battery,operation,service,cycle,now,FreshHours,expected);
            return new BatteryEvaluation(decision,JsonSerializer.Serialize(new {
                source="REAL",p.Version,p.Enabled,batteryInspectionId=battery?.Id,batteryState=battery?.BatteryState,batteryAt=battery?.ObservedAtUtc,
                operationInspectionId=operation?.Id,operationState=operation?.OperationState,operationAt=operation?.ObservedAtUtc,
                serviceId=service?.Id,serviceAt=service?.PerformedAtUtc,serviceOutcome=service?.Outcome,
                cycleServiceId=cycle?.Id,cycleAt=cycle?.PerformedAtUtc,freshHours=FreshHours,expectedCycleHours=expected,decision.Reason
            }));
        });
    }
    public async Task<BatteryEvaluation> Store(EquipmentBatteryProfile profile,DateTime now) {
        var value=(await Evaluate([profile],now))[profile.Id];
        var last=await db.BatteryAssessments.Where(x=>x.ProfileId==profile.Id && x.DataSource=="REAL").OrderByDescending(x=>x.Id).FirstOrDefaultAsync();
        if(last == null || last.EvidenceJson!=value.EvidenceJson || last.Priority!=value.Decision.Priority || last.MethodVersion!=BatteryRules.Version) {
            db.BatteryAssessments.Add(new EquipmentBatteryAssessment {ProfileId=profile.Id,AssessedAtUtc=now,EvidenceCutoffUtc=now,
                Priority=value.Decision.Priority,RecommendedAction=value.Decision.RecommendedAction,Method="RULE",MethodVersion=BatteryRules.Version,
                EvidenceJson=value.EvidenceJson,ValidUntilUtc=value.Decision.ValidUntilUtc,DataSource="REAL"});
        }
        return value;
    }
}
