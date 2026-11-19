using Xunit;

public class CatalogTests
{
    [Theory]
    [InlineData("../other.zip")]
    [InlineData("other/file.zip")]
    [InlineData("other\\file.zip")]
    [InlineData("file.zip\r\nX-Test: yes")]
    [InlineData("file.zip.uploading")]
    public void RejectsUnsafeOrIncompleteDownloads(string name) => Assert.False(BackupFileMetadata.CanDownload(name));

    [Fact]
    public void ExtractsUtcTimestampAndDifferentialFromAgentFilename()
    {
        var info = BackupFileMetadata.Parse("ATXDADOS_20261113_220136_7831741afb98480f95d7fa850e6c2bea_differential.zip");
        Assert.Equal("Differential", info.Type);
        Assert.Equal(new DateTimeOffset(2026,11,13,22,1,36,TimeSpan.Zero), info.CreatedAt);
    }

    [Fact]
    public void UnknownNamesDoNotInventBackupDateOrType()
    {
        var info = BackupFileMetadata.Parse("backup-antigo.zip");
        Assert.Null(info.CreatedAt);
        Assert.Equal("Unknown", info.Type);
        Assert.True(BackupFileMetadata.CanDownload("backup-antigo.zip"));
    }
}
