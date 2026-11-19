using BackupControl.Agent;
using Xunit;

public class SafetyTests
{
    [Theory]
    [InlineData("../cliente")]
    [InlineData("cliente/fora")]
    [InlineData("Cliente")]
    [InlineData("")]
    public void RejectsUnsafeClientDirectory(string slug) => Assert.Throws<ArgumentException>(() => Safety.RemoteDirectory("/backup", slug));
    [Fact]
    public void DirectoryHasNoDatabaseSubdirectory() => Assert.Equal("/backup/olimpico", Safety.RemoteDirectory("/backup", "olimpico"));
    [Fact]
    public void DatabaseIdentifierIsQuoted() => Assert.Equal("[Banco]]nome]", Safety.SqlIdentifier("Banco]nome"));
    [Fact]
    public void FileNamesCannotEscapeDirectory()
    {
        var name = Safety.FileName("../Banco:/X", DateTimeOffset.Parse("2026-11-13T20:00:00Z"), Guid.NewGuid(), "Full");
        Assert.DoesNotContain("/", name); Assert.DoesNotContain("\\", name); Assert.DoesNotContain("..", name);
        Assert.EndsWith("_full.zip", name);
    }
    [Fact]
    public void BackupUsesChecksumAndDifferentialOnlyWhenRequested()
    {
        Assert.Contains("WITH CHECKSUM, INIT", Safety.BackupSql("Banco", "Full"));
        Assert.Contains("DIFFERENTIAL", Safety.BackupSql("Banco", "Differential"));
        Assert.StartsWith("BACKUP LOG", Safety.BackupSql("Banco", "Log"));
        Assert.Throws<ArgumentException>(() => Safety.BackupSql("Banco", "DROP"));
    }
    [Fact]
    public async Task ProtectedStateRoundTripsWithoutPlaintextSecrets()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bc-test-" + Guid.NewGuid());
        try
        {
            var store = new ProtectedStore(dir);
            await store.Save("test", new { Password = "segredo-nao-visivel" });
            Assert.DoesNotContain("segredo-nao-visivel", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(dir, "test.dat"))));
            var read = await store.Load<Dictionary<string, string>>("test"); Assert.Equal("segredo-nao-visivel", read!["Password"]);
            Assert.True(new DirectoryInfo(dir).GetAccessControl().AreAccessRulesProtected);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public void SameInstallationCannotRunTwiceEvenWithDifferentDataDirectories()
    {
        var id = Guid.NewGuid();
        using (var first = new InstanceLease(id)) Assert.Throws<IOException>(() => new InstanceLease(id));
        using var restarted = new InstanceLease(id);
    }
}
