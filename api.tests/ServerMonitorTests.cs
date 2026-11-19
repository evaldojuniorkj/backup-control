using Xunit;
public class ServerMonitorTests
{
 static readonly DateTimeOffset Now=DateTimeOffset.Parse("2026-11-18T12:00:00Z");
 static ServerSnapshot Sample()=>new(1,Now,"fixture-server",new(1000,600,400),[new("backups","/srv/ftp/backup",10000,4000,5500,500)],"/backup",Now,true,20,[new("cliente-a",1024,1000,2,true,0),new("sem-cadastro",512,500,1,true,0)]);
 [Fact] public void AttributesBySlugIncludingPausedClientsAndSeparatesUnknownFolders()
 {
  var rows=new[]{new MetricClient(Guid.NewGuid(),"Empresa A","cliente-a",false),new MetricClient(Guid.NewGuid(),"Empresa B","cliente-b",true)};
  var view=ServerMetricsPolicy.Project(Sample(),rows,"/backup",Now);
  Assert.Equal(1024,view.Clients[0].AllocatedBytes);Assert.False(view.Clients[0].Enabled);
  Assert.Equal(532,view.UnassignedBytes);Assert.Equal(1,view.UnassignedDirectories);
  Assert.False(view.Clients[1].DirectoryExists);Assert.Equal(0,view.Clients[1].AllocatedBytes);
 }
 [Fact] public void OldOrPartialDataAndDifferentStorageAreExplicit()
 {
  var s=Sample() with{ObservedAt=Now.AddMinutes(-20),UsageObservedAt=Now.AddMinutes(-20),UsageComplete=false};
  var view=ServerMetricsPolicy.Project(s,[new(Guid.NewGuid(),"Empresa","ausente",true)],"/outro",Now);
  Assert.True(view.HostStale);Assert.True(view.UsageStale);Assert.False(view.StorageMatches);
  Assert.Null(view.Clients[0].DirectoryExists);Assert.False(view.Clients[0].Complete);
 }
 [Fact] public void InvalidSamplesAreNotPresentedAsRealMetrics()
 {
  Assert.Throws<ArgumentException>(()=>ServerMetricsPolicy.Project(Sample() with{Memory=new(100,120,0)},[],"/backup",Now));
  Assert.Throws<ArgumentException>(()=>ServerMetricsPolicy.Project(Sample() with{SchemaVersion=2},[],"/backup",Now));
  Assert.Throws<ArgumentException>(()=>ServerMetricsPolicy.Project(Sample() with{Directories=[new("a",-1,0,0,true,0)]},[],"/backup",Now));
 }
}
