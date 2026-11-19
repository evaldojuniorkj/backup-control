using System.Text.Json;

public record MetricMemory(long TotalBytes,long UsedBytes,long AvailableBytes);
public record MetricVolume(string Name,string Path,long TotalBytes,long UsedBytes,long AvailableBytes,long ReservedBytes);
public record MetricDirectory(string Slug,long AllocatedBytes,long FileBytes,long FileCount,bool Complete,int SkippedLinks);
public record ServerSnapshot(int SchemaVersion,DateTimeOffset ObservedAt,string Hostname,MetricMemory Memory,MetricVolume[] Volumes,string BackupDirectory,DateTimeOffset UsageObservedAt,bool UsageComplete,long UnassignedBytes,MetricDirectory[] Directories);
public record MetricClient(Guid Id,string Name,string Slug,bool Enabled);
public record ClientDiskUsage(Guid Id,string Name,string Slug,bool Enabled,long AllocatedBytes,long FileBytes,long FileCount,bool? DirectoryExists,bool Complete,int SkippedLinks);
public record ServerMetricsView(DateTimeOffset ObservedAt,string Hostname,MetricMemory Memory,MetricVolume[] Volumes,string BackupDirectory,DateTimeOffset UsageObservedAt,bool HostStale,bool UsageStale,bool UsageComplete,bool StorageMatches,ClientDiskUsage[] Clients,long UnassignedBytes,int UnassignedDirectories);

public static class ServerMetricsPolicy
{
 public static ServerMetricsView Project(ServerSnapshot s,IEnumerable<MetricClient> clients,string configuredDirectory,DateTimeOffset now)
 {
  if(s.SchemaVersion!=1||string.IsNullOrWhiteSpace(s.Hostname)||s.Hostname.Length>255||s.Memory is null||s.Memory.TotalBytes<=0||s.Memory.AvailableBytes<0||s.Memory.AvailableBytes>s.Memory.TotalBytes||s.Memory.UsedBytes!=s.Memory.TotalBytes-s.Memory.AvailableBytes||s.Volumes is null||s.Volumes.Length is <1 or >8||s.Directories is null||s.Directories.Length>10000||s.UnassignedBytes<0||s.ObservedAt==default||s.UsageObservedAt==default||s.ObservedAt>now.AddMinutes(5)||s.UsageObservedAt>s.ObservedAt.AddSeconds(5))throw new ArgumentException("Amostra de monitoramento inválida.");
  foreach(var v in s.Volumes)if(v is null||v.TotalBytes<=0||v.UsedBytes<0||v.AvailableBytes<0||v.AvailableBytes>v.TotalBytes||v.UsedBytes>v.TotalBytes-v.AvailableBytes||v.ReservedBytes<0||string.IsNullOrWhiteSpace(v.Name)||string.IsNullOrWhiteSpace(v.Path))throw new ArgumentException("Amostra de volume inválida.");
  if(s.Directories.Any(d=>d is null||string.IsNullOrEmpty(d.Slug)||d.Slug.Length>255||d.Slug.Contains('/')||d.Slug is "." or ".."||d.AllocatedBytes<0||d.FileBytes<0||d.FileCount<0||d.SkippedLinks<0)||s.Directories.Select(d=>d.Slug).Distinct(StringComparer.Ordinal).Count()!=s.Directories.Length)throw new ArgumentException("Amostra de diretórios inválida.");
  var folders=s.Directories.ToDictionary(d=>d.Slug,StringComparer.Ordinal);var registered=clients.ToArray();var slugs=registered.Select(c=>c.Slug).ToHashSet(StringComparer.Ordinal);
  var rows=registered.Select(c=>{folders.TryGetValue(c.Slug,out var d);return new ClientDiskUsage(c.Id,c.Name,c.Slug,c.Enabled,d?.AllocatedBytes??0,d?.FileBytes??0,d?.FileCount??0,d is not null?true:s.UsageComplete?false:null,d?.Complete??s.UsageComplete,d?.SkippedLinks??0);}).OrderByDescending(c=>c.AllocatedBytes).ThenBy(c=>c.Name).ToArray();
  var unknown=s.Directories.Where(d=>!slugs.Contains(d.Slug)).ToArray();
  var unassigned=checked(s.UnassignedBytes+unknown.Sum(d=>d.AllocatedBytes));
  return new(s.ObservedAt,s.Hostname,s.Memory,s.Volumes,s.BackupDirectory,s.UsageObservedAt,now-s.ObservedAt>TimeSpan.FromSeconds(90),now-s.UsageObservedAt>TimeSpan.FromMinutes(11),s.UsageComplete,string.Equals(s.BackupDirectory.TrimEnd('/'),configuredDirectory.TrimEnd('/'),StringComparison.Ordinal),rows,unassigned,unknown.Length);
 }
}

public sealed class ServerMonitor(IConfiguration configuration,Database db,StorageService storage)
{
 public async Task<ServerMetricsView?> Get(CancellationToken ct)
 {
  var path=configuration["SERVER_METRICS_FILE"]??System.IO.Path.Combine(System.IO.Path.GetDirectoryName(configuration["NETWORK_STATUS_FILE"]??"/app/network/status.json")!,"server-metrics.json");
  try{
   var file=new FileInfo(path);if(!file.Exists||file.Length>4*1024*1024)return null;
   await using var input=File.OpenRead(path);
   var snapshot=await JsonSerializer.DeserializeAsync<ServerSnapshot>(input,new JsonSerializerOptions(JsonSerializerDefaults.Web),ct);if(snapshot is null)return null;
   var rows=await db.Query("SELECT id,name,slug,enabled FROM clients");
   var clients=rows.Select(c=>new MetricClient((Guid)c["id"]!,(string)c["name"]!,(string)c["slug"]!,(bool)c["enabled"]!));
   return ServerMetricsPolicy.Project(snapshot,clients,(await storage.Get())?.BaseDirectory??"/backup",DateTimeOffset.UtcNow);
  }
  catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or OverflowException){return null;}
 }
}
