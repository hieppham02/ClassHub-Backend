using System.Text.Json;
namespace ClassHub_API.Services;
public record BatteryForecastInput(int DeviceKind,double AgeDays,double BorrowHoursTotal,double BorrowEventsTotal,double HoursLast7,double ActiveDaysLast7,bool Rechargeable) {
    public double[] Features => [DeviceKind,AgeDays,BorrowHoursTotal,BorrowEventsTotal,HoursLast7,ActiveDaysLast7,Rechargeable?1:0];
    public bool Valid => DeviceKind is 0 or 1 && Features.All(double.IsFinite) && AgeDays is >=0 and <=730 &&
        BorrowHoursTotal is >=0 and <=17520 && BorrowEventsTotal is >=0 and <=100000 &&
        HoursLast7 is >=0 and <=168 && ActiveDaysLast7 is >=0 and <=7 && Math.Floor(ActiveDaysLast7)==ActiveDaysLast7 &&
        Math.Floor(BorrowEventsTotal)==BorrowEventsTotal && HoursLast7<=BorrowHoursTotal && HoursLast7<=AgeDays*24+1e-6 &&
        BorrowHoursTotal<=AgeDays*24+1e-6 && HoursLast7<=ActiveDaysLast7*24+1e-6 &&
        ActiveDaysLast7<=Math.Min(7,Math.Ceiling(AgeDays)) && (HoursLast7==0?ActiveDaysLast7==0:ActiveDaysLast7>0);
}
public record BatteryForecastResult(double RemainingDays,double LowerDays,double UpperDays,DateTime SuggestedDateUtc,string Priority,string ModelVersion,string TrainingSource,bool OutsideTrainingRange);
public sealed class BatteryForecastModel {
    readonly JsonDocument document;
    readonly JsonElement core,metadata;
    public string Version {get;}
    public JsonElement Metadata=>metadata.Clone();
    public JsonElement GoldenVectors=>document.RootElement.GetProperty("goldenVectors").Clone();
    public BatteryForecastModel():this(Path.Combine(AppContext.BaseDirectory,"Models","battery_gbr.json")){}
    public BatteryForecastModel(string path) {
        document=JsonDocument.Parse(File.ReadAllText(path));
        var root=document.RootElement;
        if(root.GetProperty("schemaVersion").GetInt32()!=1)throw new InvalidDataException("Unsupported battery model.");
        core=root.GetProperty("model");metadata=root.GetProperty("metadata");Version=root.GetProperty("modelVersion").GetString()!;
    }
    public BatteryForecastResult Predict(BatteryForecastInput input,DateTime now) {
        if(!input.Valid)throw new ArgumentException("Thông số nằm ngoài phạm vi hợp lệ.");
        var features=input.Features.Select(x=>(double)(float)x).ToArray();
        var value=core.GetProperty("initial").GetDouble();var rate=core.GetProperty("learningRate").GetDouble();
        foreach(var tree in core.GetProperty("trees").EnumerateArray()) {
            var left=tree.GetProperty("left");var right=tree.GetProperty("right");var indexes=tree.GetProperty("feature");
            var thresholds=tree.GetProperty("threshold");var values=tree.GetProperty("value");int n=0;
            for(int depth=0;left[n].GetInt32()!=-1;depth++) {
                if(depth>20)throw new InvalidDataException("Invalid model tree.");
                n=features[indexes[n].GetInt32()]<=thresholds[n].GetDouble()?left[n].GetInt32():right[n].GetInt32();
            }
            value+=rate*values[n].GetDouble();
        }
        value=Math.Clamp(value,0,730);
        var delta=metadata.GetProperty("intervalAbsoluteErrorDays")[input.DeviceKind].GetDouble();
        var min=metadata.GetProperty("featureMinimum");var max=metadata.GetProperty("featureMaximum");
        var outside=features.Where((v,i)=>v<min[i].GetDouble() || v>max[i].GetDouble()).Any();
        return new(Math.Round(value,2),Math.Round(Math.Max(0,value-delta),2),Math.Round(Math.Min(730,value+delta),2),
            now.AddDays(value),value<=7?"HIGH":value<=21?"MEDIUM":"LOW",Version,"SYNTHETIC",outside);
    }
}
