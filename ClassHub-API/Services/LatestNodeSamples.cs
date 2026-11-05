using ClassHub_API.Models;
namespace ClassHub_API.Services;

/// <summary>Groupwise latest sample, without a correlated ORDER BY for every telemetry row.</summary>
public static class LatestNodeSamples {
    public static IQueryable<IotSensor> Query(IQueryable<IotSensor> samples) {
        var times=samples.GroupBy(s=>new{s.NodeId,s.DataSource})
            .Select(g=>new{g.Key.NodeId,g.Key.DataSource,ReceivedAt=g.Max(s=>s.ReceivedAt)});
        // Equal millisecond timestamps are ordered by Id, matching history and analysis endpoints.
        var ids=from s in samples
                join t in times on new{s.NodeId,s.DataSource,s.ReceivedAt} equals new{t.NodeId,t.DataSource,t.ReceivedAt}
                group s by new{s.NodeId,s.DataSource} into g
                select g.Max(s=>s.Id);
        return from s in samples join id in ids on s.Id equals id select s;
    }
}
