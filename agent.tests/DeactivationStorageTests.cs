using BackupControl.Agent;
using Xunit;
public class DeactivationStorageTests
{
    [Fact]
    public async Task ErasesSqlAndTemporaryCachesButPreservesLocalBackupPayloads()
    {
        var root=Path.Combine(Path.GetTempPath(),"agent-disable-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            var store=new ProtectedStore(root);var token=new string('a',48);
            await store.Save("settings",new LocalSettings("https://central.example",token,"Server=localhost;User ID=sa;Password=sql-secret",Path.Combine(root,"backups")));
            await store.Write(new AgentState{DeactivationId=Guid.NewGuid(),Suspended=true});
            File.WriteAllText(Path.Combine(root,"installer.dat"),"obsolete");
            var orphan=Path.Combine(root,"settings.dat."+Guid.NewGuid().ToString("N")+".tmp");File.WriteAllText(orphan,"obsolete SQL cache");
            Directory.CreateDirectory(Path.Combine(root,"backups"));var payload=Path.Combine(root,"backups","preserve.zip");File.WriteAllText(payload,"local payload");
            await store.ClearBackupCredentials(false,default);
            var s=await store.Load<LocalSettings>("settings");Assert.Equal("",s!.SqlConnectionString);Assert.False(s.EnableSchedule);Assert.Equal(token,s.AgentToken);
            Assert.False(File.Exists(orphan));Assert.False(File.Exists(Path.Combine(root,"installer.dat")));Assert.True(File.Exists(payload));
            await store.ClearBackupCredentials(true,default);Assert.Equal("",(await store.Load<LocalSettings>("settings"))!.AgentToken);
            Assert.NotNull((await store.Read()).DeactivationId);
        } finally {Directory.Delete(root,true);}
    }
}
