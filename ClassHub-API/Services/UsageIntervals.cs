namespace ClassHub_API.Services;
public record UsageInterval(DateTime Start,DateTime End);
public record UsageSlot(int Id,string Name,TimeSpan Start,TimeSpan End);
public record UsageSession(int Id,string RoomId,DateTime Start,DateTime End);
public static class UsageIntervals {
    public static List<UsageInterval> Merge(IEnumerable<UsageInterval> input) {
        var result=new List<UsageInterval>();
        foreach(var item in input.Where(x=>x.End>x.Start).OrderBy(x=>x.Start)) {
            if(result.Count==0 || item.Start>result[^1].End)result.Add(item);
            else if(item.End>result[^1].End)result[^1]=new(result[^1].Start,item.End);
        }
        return result;
    }
    public static double Hours(IEnumerable<UsageInterval> input)=>Merge(input).Sum(x=>(x.End-x.Start).TotalHours);
    public static double Overlap(IEnumerable<UsageInterval> use,IEnumerable<UsageInterval> windows)=>
        Hours(from a in Merge(use) from b in Merge(windows)
            let start=a.Start>b.Start?a.Start:b.Start let end=a.End<b.End?a.End:b.End
            where end>start select new UsageInterval(start,end));
    public static List<UsageInterval> Windows(DateTime day,IReadOnlyList<UsageSlot> slots)=>
        slots.Take(5).Select(s=>new UsageInterval(day.Date+s.Start,day.Date+s.End+(s.End<=s.Start?TimeSpan.FromDays(1):TimeSpan.Zero))).ToList();
    public static int UsedSlots(IEnumerable<UsageInterval> use,IReadOnlyList<UsageInterval> windows) =>
        windows.Take(5).Count(w=>Overlap(use,[w])>0);
}
