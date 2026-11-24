using System.Net.Sockets;
using System.Security.Cryptography;
using FluentFTP;
using FluentFTP.Exceptions;
namespace BackupControl.Agent;

public sealed class FtpUploader : IUploader
{
    public async Task<bool?> Exists(string directory,string fileName,long size,StorageConfig storage,CancellationToken ct)
    {
        if(string.IsNullOrEmpty(fileName)||fileName.IndexOfAny(['/', '\\'])>=0||Path.GetFileName(fileName)!=fileName||!fileName.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)||size<=0)return false;
        using var ftp=Client(storage);await ftp.Connect(ct);
        try{var path=directory.TrimEnd('/')+"/"+fileName;return await ftp.FileExists(path,ct)&&await ftp.GetFileSize(path,-1,ct)==size;}
        finally{await ftp.Disconnect(ct);}
    }
    private static AsyncFtpClient Client(StorageConfig s)
    {
        if (s.Protocol != "FTP") throw new ArgumentException("Protocolo não suportado.");
        var ftp = new AsyncFtpClient(s.Host, s.Username, s.Password, s.Port);
        ftp.Config.ConnectTimeout = 15000; ftp.Config.ReadTimeout = 30000;
        ftp.Config.DataConnectionConnectTimeout = 15000; ftp.Config.DataConnectionReadTimeout = 60000;
        ftp.Config.DataConnectionType = FtpDataConnectionType.EPSV;
        return ftp;
    }
    public async Task Check(string directory, StorageConfig storage, CancellationToken ct)
    {
        using var ftp = Client(storage); await ftp.Connect(ct);
        var probe = directory + "/connection-test-" + Guid.NewGuid().ToString("N") + ".txt";
        try
        {
            await ftp.CreateDirectory(directory, ct);
            using var stream = new MemoryStream("BackupControlAgent probe"u8.ToArray());
            if (await ftp.UploadStream(stream, probe, FtpRemoteExists.Overwrite, false, null, ct) != FtpStatus.Success) throw new IOException("Escrita FTP não confirmada.");
        }
        finally
        {
            try { if (ftp.IsConnected && await ftp.FileExists(probe, ct)) await ftp.DeleteFile(probe, ct); }
            finally { await ftp.Disconnect(ct); }
        }
    }
    public async Task Upload(Operation op, StorageConfig storage, CancellationToken ct)
    {
        if (!File.Exists(op.ZipPath) || new FileInfo(op.ZipPath).Length != op.SizeBytes) throw new BackupUploadRejectedException("ZIP local ausente ou com tamanho divergente.");
        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var phase = "abertura da conexão";
            try
            {
                using var ftp = Client(storage); await ftp.Connect(ct);
                try
                {
                    phase = "preparação do destino";
                    await ftp.CreateDirectory(op.Directory, ct);
                    var final = op.Directory + "/" + op.FileName;
                    var pending = final + ".uploading";
                    if (await ftp.FileExists(final, ct))
                    {
                        if (await ftp.GetFileSize(final, -1, ct) != op.SizeBytes)
                            throw new BackupUploadRejectedException("Já existe arquivo final com tamanho divergente. O arquivo existente foi preservado.");
                        phase = "verificação do ZIP final existente";
                        await VerifyRemoteZip(ftp, final, op, ct);
                        return;
                    }

                    long partialSize = 0;
                    if (await ftp.FileExists(pending, ct))
                    {
                        partialSize = await ftp.GetFileSize(pending, -1, ct);
                        if (partialSize < 0) throw new IOException("Tamanho parcial indisponível.");
                        if (partialSize > op.SizeBytes)
                            throw new BackupUploadRejectedException("O ZIP parcial no FTP é maior que o ZIP local. Os arquivos foram preservados.");
                    }
                    if (partialSize > 0 && partialSize < op.SizeBytes)
                    {
                        phase = "verificação dos bytes já enviados";
                        await VerifyPartialPrefix(ftp, pending, op.ZipPath, partialSize, ct);
                        if (await ftp.GetFileSize(pending, -1, ct) != partialSize)
                            throw new IOException("O tamanho do arquivo temporário mudou durante a verificação.");
                    }
                    if (partialSize < op.SizeBytes)
                    {
                        phase = "transferência dos dados";
                        var mode = partialSize > 0 ? FtpRemoteExists.Resume : FtpRemoteExists.NoCheck;
                        var result = await ftp.UploadFile(op.ZipPath, pending, mode, false, FtpVerify.None, null, ct);
                        if (result != FtpStatus.Success && !(partialSize > 0 && result == FtpStatus.Skipped))
                            throw new IOException("Envio FTP não concluído.");
                    }
                    phase = "verificação do ZIP temporário";
                    if (await ftp.GetFileSize(pending, -1, ct) != op.SizeBytes) throw new IOException("Tamanho remoto divergente.");
                    if (partialSize > 0) await VerifyRemoteZip(ftp, pending, op, ct);
                    phase = "confirmação do arquivo final";
                    await ftp.Rename(pending, final, ct);
                    if (await ftp.GetFileSize(final, -1, ct) != op.SizeBytes) throw new IOException("Arquivo final não confirmado.");
                    return;
                }
                finally { await ftp.Disconnect(ct); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (BackupUploadRejectedException) { throw; }
            catch (Exception error) when (error is IOException or TimeoutException or SocketException or FtpException)
            {
                if (attempt == attempts) throw new FtpTransferException(phase, attempt, attempts, error);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 3), ct);
            }
        }
    }

    private static async Task VerifyPartialPrefix(AsyncFtpClient ftp, string path, string localPath, long size, CancellationToken ct)
    {
        await using var remote = await ftp.OpenRead(path, FtpDataType.Binary, 0, false, ct);
        await using var local = File.OpenRead(localPath);
        var remoteBytes = new byte[65536];
        var localBytes = new byte[remoteBytes.Length];
        for (var remaining = size; remaining > 0;)
        {
            var length = (int)Math.Min(remaining, remoteBytes.Length);
            await remote.ReadExactlyAsync(remoteBytes.AsMemory(0, length), ct);
            await local.ReadExactlyAsync(localBytes.AsMemory(0, length), ct);
            if (!remoteBytes.AsSpan(0, length).SequenceEqual(localBytes.AsSpan(0, length)))
                throw new BackupUploadRejectedException("O ZIP parcial no FTP diverge do ZIP local. Os arquivos foram preservados.");
            remaining -= length;
        }
    }

    private static async Task VerifyRemoteZip(AsyncFtpClient ftp, string path, Operation op, CancellationToken ct)
    {
        var expected = op.Sha256;
        if (expected is not { Length: 64 } || !expected.All(Uri.IsHexDigit))
        {
            // Pending work from agents without a stored hash can still be verified against the local ZIP.
            await using var local = File.OpenRead(op.ZipPath);
            expected = Convert.ToHexString(await SHA256.HashDataAsync(local, ct));
        }
        string? actual = null;
        if (ftp.HasFeature(FtpCapability.HASH) || ftp.HasFeature(FtpCapability.XSHA256))
        {
            try
            {
                var checksum = await ftp.GetChecksum(path, FtpHashAlgorithm.SHA256, ct);
                if (checksum.IsValid && checksum.Algorithm == FtpHashAlgorithm.SHA256) actual = checksum.Value;
            }
            catch (FtpHashUnsupportedException) { }
        }
        if (actual is null)
        {
            await using var remote = await ftp.OpenRead(path, FtpDataType.Binary, 0, false, ct);
            actual = Convert.ToHexString(await SHA256.HashDataAsync(remote, ct));
        }
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new BackupUploadRejectedException("O SHA-256 do ZIP no FTP diverge do ZIP local. Os arquivos foram preservados.");
    }
}

internal sealed class BackupUploadRejectedException(string message) : IOException(message);

internal sealed class FtpTransferException(string phase, int attempt, int attempts, Exception cause)
    : IOException($"Falha FTP em {phase} ({Describe(cause)}, tentativa {attempt} de {attempts}).", cause)
{
    private static string Describe(Exception cause)
    {
        for (Exception? error = cause; error is not null; error = error.InnerException)
        {
            if (error is TimeoutException || error is SocketException { SocketErrorCode: SocketError.TimedOut })
                return "tempo limite alcançado";
            if (error is FtpCommandException { CompletionCode: { Length: 3 } code } && code.All(char.IsAsciiDigit))
                return "resposta FTP " + code;
            if (error is SocketException socket) return "erro de conexão " + socket.SocketErrorCode;
        }
        return "etapa não confirmada";
    }
}
