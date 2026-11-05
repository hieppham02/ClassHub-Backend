using System.Text.Json;
namespace ClassHub_API.Services;

/// <summary>Inference of an exported sklearn forest. Synthetic training, advisory only.</summary>
public sealed class IotAnomalyModel {
    readonly JsonDocument document;
    public IotAnomalyModel():this(Path.Combine(AppContext.BaseDirectory,"Models","iot_forest.json")){}
    public IotAnomalyModel(string path) { document=JsonDocument.Parse(File.ReadAllText(path)); }
    public string Version=>document.RootElement.GetProperty("modelVersion").GetString()!;
    public double[] Predict(double temperature,double humidity,double current,bool active) {
        double[] features=[(float)temperature,(float)humidity,(float)current,active?1:0];
        if(features.Any(v=>!double.IsFinite(v)))throw new ArgumentException("Nonfinite features");
        double[] result=new double[4];
        var trees=document.RootElement.GetProperty("trees");
        foreach(var tree in trees.EnumerateArray()) {
            int n=0;
            for(int depth=0;tree.GetProperty("left")[n].GetInt32()!=-1;depth++){
                if(depth>20)throw new InvalidDataException("Invalid forest");
                var feature=tree.GetProperty("feature")[n].GetInt32();
                n=tree.GetProperty(features[feature]<=tree.GetProperty("threshold")[n].GetDouble()?"left":"right")[n].GetInt32();
            }
            for(int k=0;k<4;k++)result[k]+=tree.GetProperty("value")[n][k].GetDouble()/trees.GetArrayLength();
        }
        return result;
    }
}
