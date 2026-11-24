using System.Security.Cryptography;
using BackupControl.Agent;
using Xunit;

public sealed class FtpResumeTests : IDisposable
{
    private readonly string localZip = Path.GetTempFileName();
    private static readonly byte[] Archive = Enumerable.Range(0, 1024).Select(i => (byte)(i % 251)).ToArray();
    private const string Directory = "/backup/fixture";
    private const string Name = "differential.zip";
    private const string Final = Directory + "/" + Name;
    private const string Partial = Final + ".uploading";

    [Theory]
    [InlineData(0, "STOR", 1024)]
    [InlineData(512, "APPE", 512)]
    [InlineData(1024, "", 0)]
    public async Task ValidPartialIsResumedAndPublishedWithoutReplacingRemoteBytes(
        int initialBytes, string expectedCommand, int expectedBytes)
    {
        await using var ftp = new LoopbackBackupFtp();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var operation = await CreateOperation();
        if (initialBytes > 0) ftp.Put(Partial, Archive[..initialBytes], DateTimeOffset.UtcNow);

        await new FtpUploader().Upload(operation, Destination(ftp), timeout.Token);

        if (expectedBytes == 0) Assert.Empty(ftp.Transfers);
        else Assert.Equal((expectedCommand, expectedBytes), Assert.Single(ftp.Transfers));
        Assert.Equal(Archive, ftp.Get(Final));
        Assert.DoesNotContain(Partial, ftp.Paths);
        Assert.Empty(ftp.Deleted);
    }

    [Fact]
    public async Task InvalidPrefixIsPreservedAndNeverPublished()
    {
        await using var ftp = new LoopbackBackupFtp();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var operation = await CreateOperation();
        byte[] invalid = [255, 254, 253];
        ftp.Put(Partial, invalid, DateTimeOffset.UtcNow);

        await Assert.ThrowsAnyAsync<IOException>(() => new FtpUploader().Upload(operation, Destination(ftp), timeout.Token));

        Assert.Equal(invalid, ftp.Get(Partial));
        Assert.DoesNotContain(Final, ftp.Paths);
        Assert.Empty(ftp.Transfers);
        Assert.Empty(ftp.Deleted);
    }

    [Fact]
    public async Task ResumeMustVerifyTheEntireRemoteZipBeforeRenamingIt()
    {
        await using var ftp = new LoopbackBackupFtp { CorruptOnAppend = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var operation = await CreateOperation();
        ftp.Put(Partial, Archive[..512], DateTimeOffset.UtcNow);

        await Assert.ThrowsAnyAsync<IOException>(() => new FtpUploader().Upload(operation, Destination(ftp), timeout.Token));

        Assert.Equal(Archive.Length, ftp.Get(Partial).Length);
        Assert.DoesNotContain(Final, ftp.Paths);
        Assert.Empty(ftp.Deleted);
    }

    [Fact]
    public async Task ExistingFinalWithSameSizeAndDifferentContentIsNeverAccepted()
    {
        await using var ftp = new LoopbackBackupFtp();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var operation = await CreateOperation();
        var differentArchive = Archive.ToArray();
        differentArchive[0] ^= 0xff;
        ftp.Put(Final, differentArchive, DateTimeOffset.UtcNow);

        var error = await Assert.ThrowsAnyAsync<IOException>(() => new FtpUploader().Upload(operation, Destination(ftp), timeout.Token));

        Assert.Contains("SHA-256", Safety.SafeError(error));
        Assert.Equal(differentArchive, ftp.Get(Final));
        Assert.Empty(ftp.Transfers);
        Assert.Empty(ftp.Deleted);
    }

    private async Task<Operation> CreateOperation()
    {
        await File.WriteAllBytesAsync(localZip, Archive);
        return new Operation(Guid.NewGuid(), null, "Differential", DateTimeOffset.UtcNow,
            "Fixture", Directory, "", localZip, Name, "ZipReady", Archive.Length,
            Convert.ToHexString(SHA256.HashData(Archive)));
    }

    private static StorageConfig Destination(LoopbackBackupFtp ftp) =>
        new("127.0.0.1", ftp.Port, "fixture", "fixture", "FTP", "EPSV");

    public void Dispose() => File.Delete(localZip);
}
