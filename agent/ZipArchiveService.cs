using System.IO.Compression;
using System.Security.Cryptography;
namespace BackupControl.Agent;

public sealed class ZipArchiveService : IArchive
{
    public async Task<(long Size, string Sha256)> Create(Operation op, CancellationToken ct)
    {
        var temporary = op.ZipPath + ".tmp";
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            using var archive = new ZipArchive(output, ZipArchiveMode.Create, true);
            var entry = archive.CreateEntry(Path.GetFileName(op.BakPath), CompressionLevel.Optimal);
            await using var target = entry.Open();
            await using var source = new FileStream(op.BakPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            await source.CopyToAsync(target, ct);
        }
        File.Move(temporary, op.ZipPath, true);
        await using var zip = new FileStream(op.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(zip, ct)).ToLowerInvariant();
        return (zip.Length, hash);
    }
}
