using BackupControl.Setup;
using Xunit;
namespace BackupControl.Setup.Tests;
public class RemoteUpdateTests
{
    [Fact]
    public void SchedulerFileUsesMatchingUnicodeDeclarationAndBom()
    {
        var path=Path.Combine(Path.GetTempPath(),"task-encoding-"+Guid.NewGuid().ToString("N")+".xml");
        try {
            UpdaterRegistration.WriteTaskDefinition(path,@"C:\Program Files\BackupControlAgentUpdater\BackupControl.Setup.exe");
            var bytes=File.ReadAllBytes(path);
            Assert.Equal(new byte[]{0xff,0xfe},bytes.Take(2).ToArray());
            var xml=System.Xml.Linq.XDocument.Load(path);
            Assert.Equal("UTF-16",xml.Declaration!.Encoding,ignoreCase:true);
            System.Xml.Linq.XNamespace ns="http://schemas.microsoft.com/windows/2004/02/mit/task";
            Assert.Equal("S-1-5-18",xml.Descendants(ns+"UserId").Single().Value);
            Assert.Equal("--apply-pending-update",xml.Descendants(ns+"Arguments").Single().Value);
        } finally {File.Delete(path);}
    }
    private sealed class Fake:IUpdateReplacement
    {
        public bool Busy,BusyAfterStop,FailStart,FailReplace,FailStop;public List<string> Calls=[];private bool stopped;private bool failed;public bool Journal;
        public Task<bool> Idle(CancellationToken ct)=>Task.FromResult(!Busy&&!(stopped&&BusyAfterStop));
        public Task Stop(CancellationToken ct){Calls.Add("stop");stopped=true;if(FailStop)throw new IOException();return Task.CompletedTask;}
        public void Snapshot(){Journal=true;Calls.Add("snapshot");}
        public void Commit()=>Journal=false;
        public void Replace(){Calls.Add("replace");if(FailReplace)throw new IOException();}
        public void Restore()=>Calls.Add("restore");
        public Task StartAndVerify(CancellationToken ct){Calls.Add("start");if(FailStart&&!failed){failed=true;throw new IOException();}return Task.CompletedTask;}
        public void EndMaintenance()=>Calls.Add("end");
    }
    [Fact]public async Task BusyBackupOrOutboxNeverStops(){var f=new Fake{Busy=true};Assert.Equal("Waiting",await UpdateReplacement.Apply(f,default));Assert.Empty(f.Calls);}
    [Fact]public async Task RechecksIdleAfterMaintenance(){var f=new Fake{BusyAfterStop=true};Assert.Equal("Waiting",await UpdateReplacement.Apply(f,default));Assert.Equal(new[]{"snapshot","stop","start","end"},f.Calls);}
    [Fact]public async Task HealthyReplacementKeepsNewFiles(){var f=new Fake();Assert.Equal("Succeeded",await UpdateReplacement.Apply(f,default));Assert.Equal(new[]{"snapshot","stop","replace","start","end"},f.Calls);}
    [Fact]public async Task StartupFailureRestoresBeforeRestart(){var f=new Fake{FailStart=true};Assert.Equal("RolledBack",await UpdateReplacement.Apply(f,default));Assert.Equal(new[]{"snapshot","stop","replace","start","restore","start","end"},f.Calls);}
    [Fact]public async Task PartialReplacementAlsoRollsBack(){var f=new Fake{FailReplace=true};Assert.Equal("RolledBack",await UpdateReplacement.Apply(f,default));Assert.Equal(new[]{"snapshot","stop","replace","restore","start","end"},f.Calls);}
    private static RemoteUpdateJob Job(string version="0.8.0",string? url=null)=>new(Guid.NewGuid(),version,new string('a',64),url??"https://central.example/api/agent/package/"+new string('a',64),"Pending");
    [Theory][InlineData("0.6.0")][InlineData("0.8.0")][InlineData("invalid")]public void RejectsNonUpgrades(string version)=>Assert.Throws<SetupException>(()=>RemoteUpdater.ValidateJob(Job(version),"https://central.example","0.8.0"));
    [Theory][InlineData("http://central.example/api/agent/package/")][InlineData("https://attacker.example/api/agent/package/")][InlineData("https://central.example/elsewhere/")]public void RejectsUntrustedDownloadLocation(string prefix)=>Assert.Throws<SetupException>(()=>RemoteUpdater.ValidateJob(Job(url:prefix+new string('a',64)),"https://central.example","0.6.0"));
    [Fact]public void AcceptsPinnedSameOriginUpgrade()=>RemoteUpdater.ValidateJob(Job(),"https://central.example","0.6.0");
    [Fact]public async Task TestInstallDoesNotTouchProductionTask()=>await UpdaterRegistration.Ensure(new("invalid","invalid","test",true),default);
    [Theory][InlineData("BCGEN001")][InlineData("BCSETUP1")]
    public void ProtectedHelperStripsBootstrapIncludingEnrollmentSecrets(string marker)
    {
        using var stream=new MemoryStream();stream.Write(new byte[100]);stream.Write("secret"u8);var footer=new byte[16];System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(footer,6);System.Text.Encoding.ASCII.GetBytes(marker).CopyTo(footer,8);stream.Write(footer);
        Assert.Equal(100,UpdaterRegistration.TemplateLength(stream));Assert.Equal(0,stream.Position);
    }
    [Fact]public async Task InterruptedReplacementRestoresBeforeStarting()
    {
        var f=new Fake();await RemoteUpdater.RecoverReplacement(f,default);Assert.Equal(new[]{"stop","restore","start","end"},f.Calls);
    }
    [Fact]public async Task RecoveryNeverOverwritesAnActiveBackupOrOutbox()
    {
        var f=new Fake{Busy=true};await Assert.ThrowsAsync<SetupException>(()=>RemoteUpdater.RecoverReplacement(f,default));Assert.Equal(new[]{"end"},f.Calls);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ReleaseMetadataMustMatchActualAgentAssembly(bool mismatch)
    {
        var directory=Path.Combine(Path.GetTempPath(),"updater-release-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try{
            var assembly=typeof(BackupControl.Agent.ProtectedStore).Assembly;var version=assembly.GetName().Version!;var target=$"{version.Major}.{version.Minor}.{version.Build}";
            File.Copy(assembly.Location,Path.Combine(directory,"BackupControl.Agent.dll"));File.WriteAllText(Path.Combine(directory,"BackupControl.Agent.exe"),"");
            var declared=mismatch?"99.99.99":target;File.WriteAllText(Path.Combine(directory,"agent-release.json"),System.Text.Json.JsonSerializer.Serialize(new{version=declared,updateProtocol=1}));
            if(mismatch)Assert.Throws<SetupException>(()=>RemoteUpdater.ValidateRelease(directory,declared));else RemoteUpdater.ValidateRelease(directory,target);
        }finally{Directory.Delete(directory,true);}
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ReleaseRejectsMissingOrInvalidAgentDll(bool invalid)
    {
        var directory=Path.Combine(Path.GetTempPath(),"updater-release-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try{File.WriteAllText(Path.Combine(directory,"BackupControl.Agent.exe"),"");File.WriteAllText(Path.Combine(directory,"agent-release.json"),"{\"version\":\"0.8.0\",\"updateProtocol\":1}");if(invalid)File.WriteAllText(Path.Combine(directory,"BackupControl.Agent.dll"),"invalid PE");Assert.Throws<SetupException>(()=>RemoteUpdater.ValidateRelease(directory,"0.8.0"));}
        finally{Directory.Delete(directory,true);}
    }
    [Fact]public async Task StopWaitFailureAttemptsSafeRestartWithoutReplacement()
    {
        var f=new Fake{FailStop=true};await Assert.ThrowsAsync<IOException>(()=>UpdateReplacement.Apply(f,default));Assert.Equal(new[]{"snapshot","stop","start","end"},f.Calls);
    }
    [Fact]public async Task ApplyingIsPostedOnlyAfterDurableSnapshotAndIdleStop()
    {
        var f=new Fake();await UpdateReplacement.Apply(f,default,()=>{f.Calls.Add("applying");return Task.CompletedTask;});Assert.Equal(new[]{"snapshot","stop","applying","replace","start","end"},f.Calls);
    }
    [Fact]public async Task BackupStartingDuringDownloadNeverPostsApplying()
    {
        var f=new Fake{BusyAfterStop=true};var posted=false;Assert.Equal("Waiting",await UpdateReplacement.Apply(f,default,()=>{posted=true;return Task.CompletedTask;}));Assert.False(posted);Assert.Equal(new[]{"snapshot","stop","start","end"},f.Calls);
    }
    [Fact]public async Task StopTimeoutWithBusyBackupNeverRestoresOrReplacesFiles()
    {
        var f=new Fake{FailStop=true,BusyAfterStop=true};await Assert.ThrowsAsync<IOException>(()=>UpdateReplacement.Apply(f,default));Assert.Equal(new[]{"snapshot","stop","start","end"},f.Calls);
    }
    [Theory][InlineData("Applying")][InlineData("Verifying")]
    public async Task CrashAfterCommitReconcilesHealthyTargetWithoutReplacement(string state)
    {
        var f=new Fake();var reports=new List<string>();await RemoteUpdater.ReconcileCompleted(f,state,s=>{reports.Add(s);return Task.CompletedTask;},default);Assert.Equal(new[]{"start","end"},f.Calls);Assert.Equal(state=="Applying"?new[]{"Verifying","Succeeded"}:new[]{"Succeeded"},reports);
    }
    [Fact]public async Task ReconciliationStatusFailurePreservesHealthyTarget()
    {
        var f=new Fake();await RemoteUpdater.ReconcileCompleted(f,"Applying",_=>throw new HttpRequestException(),default);Assert.Equal(new[]{"start","end"},f.Calls);
    }
    [Fact]public async Task JournalBeforeStopSurvivesInterruptionAndSupportsRecovery()
    {
        var f=new Fake{FailStop=true,FailStart=true};await Assert.ThrowsAsync<IOException>(()=>UpdateReplacement.Apply(f,default));Assert.True(f.Journal);Assert.Equal(new[]{"snapshot","stop","start","end"},f.Calls);
        f.FailStop=false;f.Calls.Clear();await RemoteUpdater.RecoverReplacement(f,default);Assert.False(f.Journal);Assert.Equal(new[]{"stop","restore","start","end"},f.Calls);
    }
    [Fact]public async Task BusyAfterStopDiscardsUnusedJournalOnlyAfterOldServiceVerified()
    {
        var f=new Fake{BusyAfterStop=true};Assert.Equal("Waiting",await UpdateReplacement.Apply(f,default));Assert.False(f.Journal);Assert.Equal(new[]{"snapshot","stop","start","end"},f.Calls);
    }
}
