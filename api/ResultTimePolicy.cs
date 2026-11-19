public sealed record ResolvedResultTime(DateTimeOffset StartedAt,DateTimeOffset FinishedAt,string Quality,double? AdjustmentSeconds);
public static class ClockSamplePolicy
{
    public static DateTimeOffset? Select(AgentTelemetry? t)=>t?.AgentTime??(t is {CurrentBackupType:null,CurrentPhase:null}?t.ConfigAppliedAt:null);
}
public static class ResultTimePolicy
{
    public static ResolvedResultTime Resolve(DateTimeOffset start,DateTimeOffset finish,DateTimeOffset received,double? skew,DateTimeOffset? sampledAt)
    {
        if(skew.HasValue && Math.Abs(skew.Value)>300 && Math.Abs(skew.Value)<=86400 && sampledAt.HasValue && received-sampledAt.Value<TimeSpan.FromMinutes(10) && received>=sampledAt.Value){
            try {
                var adjustedStart=start.AddSeconds(-skew.Value);var adjustedFinish=finish.AddSeconds(-skew.Value);
                if(adjustedFinish<=received.AddMinutes(10))return new(adjustedStart,adjustedFinish,"ClockAdjusted",skew);
            }catch(ArgumentOutOfRangeException){ /* Keep the reported range instead of overflowing on malformed extreme dates. */ }
        }
        if(finish>received.AddMinutes(10))return new(received-(finish-start),received,"ReceiptEstimated",null);
        return new(start,finish,"AgentClock",null);
    }
}
