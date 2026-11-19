using System.Globalization;
namespace BackupControl.Agent;
public static class ChangeMarkerPolicy
{
 public static bool Unchanged(DatabaseChangeStamp? previous,DatabaseChangeStamp? current,string? fullBase)
 {
  if(previous is null||current is null||current.DatabaseGuid==Guid.Empty||current.RecoveryForkGuid==Guid.Empty||string.IsNullOrWhiteSpace(current.LogEndLsn)||previous.DatabaseGuid!=current.DatabaseGuid||previous.RecoveryForkGuid!=current.RecoveryForkGuid||!string.Equals(previous.LogEndLsn,current.LogEndLsn,StringComparison.Ordinal))return false;
  return decimal.TryParse(current.FullBaseLsn,NumberStyles.Number,CultureInfo.InvariantCulture,out var actual)&&decimal.TryParse(fullBase,NumberStyles.Number,CultureInfo.InvariantCulture,out var expected)&&actual>0&&actual==expected;
 }
}
