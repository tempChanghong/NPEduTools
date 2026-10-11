namespace NPEduTools.Core;

// A process disappearance is an observation, not proof of who terminated it.
public sealed record GuardRecoveryEvent(Guid Id,Guid Generation,GuardProcess App,GuardProcess? Host,
    DateTimeOffset DetectedUtc,DateTimeOffset WindowEndUtc,string ContextKey)
{
    public static string Key(string scope,string version)=>Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope+"|"+version)));
    public bool Valid()=>ContextKey is {Length:64} && ContextKey.All(Uri.IsHexDigit) && Id!=Guid.Empty && Generation!=Guid.Empty && App is {Id:>0,StartedUtcTicks:>0} &&
        (Host is null || Host is {Id:>0,StartedUtcTicks:>0}) && DetectedUtc.Offset==TimeSpan.Zero && WindowEndUtc.Offset==TimeSpan.Zero &&
        WindowEndUtc>DetectedUtc && WindowEndUtc-DetectedUtc<=TimeSpan.FromHours(3);
}
