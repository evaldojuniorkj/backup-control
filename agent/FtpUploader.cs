using FluentFTP;
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
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var ftp = Client(storage); await ftp.Connect(ct);
                try
                {
                    await ftp.CreateDirectory(op.Directory, ct);
                    var final = op.Directory + "/" + op.FileName;
                    var pending = final + ".uploading";
                    if (await ftp.FileExists(final, ct))
                    {
                        if (await ftp.GetFileSize(final, -1, ct) == op.SizeBytes) return;
                        throw new BackupUploadRejectedException("Já existe arquivo final com tamanho divergente. O arquivo existente foi preservado.");
                    }
                    if (await ftp.UploadFile(op.ZipPath, pending, FtpRemoteExists.Overwrite, false, FtpVerify.None, null, ct) != FtpStatus.Success) throw new IOException("Envio FTP não concluído.");
                    if (await ftp.GetFileSize(pending, -1, ct) != op.SizeBytes) throw new IOException("Tamanho remoto divergente.");
                    await ftp.Rename(pending, final, ct);
                    if (await ftp.GetFileSize(final, -1, ct) != op.SizeBytes) throw new IOException("Arquivo final não confirmado.");
                    return;
                }
                finally { await ftp.Disconnect(ct); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (BackupUploadRejectedException) { throw; }
            catch when (attempt < 3) { await Task.Delay(TimeSpan.FromSeconds(attempt * 3), ct); }
        }
    }
}

internal sealed class BackupUploadRejectedException(string message) : IOException(message);
