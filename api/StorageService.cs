using FluentFTP;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;

public sealed class StorageService(Database db, IDataProtectionProvider provider)
{
    private readonly IDataProtector protector = provider.CreateProtector("backup-control-ftp-v1");
    public async Task<Storage?> Get()
    {
        var rows=await db.Query("SELECT payload FROM settings WHERE id=1");
        return rows.Count==0 ? null : JsonSerializer.Deserialize<Storage>((string)rows[0]["payload"]!);
    }
    public string Password(Storage s)=>protector.Unprotect(s.EncryptedPassword);
    public async Task Save(StorageInput input)
    {
        var old=await Get();
        var encrypted=string.IsNullOrEmpty(input.Password) ? old?.EncryptedPassword ?? "" : protector.Protect(input.Password);
        if(encrypted.Length==0) throw new ArgumentException("Informe a senha FTP.");
        var storage=new Storage(input.Host.Trim(),input.Port,input.Username.Trim(),encrypted,input.BaseDirectory.TrimEnd('/'));
        await db.Execute("INSERT INTO settings(id,payload) VALUES(1,$1) ON CONFLICT(id) DO UPDATE SET payload=EXCLUDED.payload",JsonSerializer.Serialize(storage));
    }
    public async Task<object> Test(string? slug, CancellationToken ct)
    {
        var storage=await Get() ?? throw new ArgumentException("Configure o destino FTP primeiro.");
        using var ftp=new AsyncFtpClient(storage.Host,storage.Username,Password(storage),storage.Port);
        ftp.Config.ConnectTimeout=10000; ftp.Config.ReadTimeout=10000;
        ftp.Config.DataConnectionConnectTimeout=10000; ftp.Config.DataConnectionReadTimeout=10000;
        ftp.Config.DataConnectionType=FtpDataConnectionType.EPSV;
        await ftp.Connect(ct);
        var directory=storage.BaseDirectory+(slug is null?"":"/"+slug);
        await ftp.CreateDirectory(directory,ct);
        var probe=directory+"/connection-test-"+Guid.NewGuid().ToString("N")+".txt";
        try
        {
            using var stream=new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Backup Control connection test"));
            var result=await ftp.UploadStream(stream,probe,FtpRemoteExists.Overwrite,false,null,ct);
            if(result!=FtpStatus.Success) throw new IOException("O FTP não confirmou a escrita do arquivo de teste.");
        }
        finally
        {
            try { if(ftp.IsConnected && await ftp.FileExists(probe,ct)) await ftp.DeleteFile(probe,ct); }
            finally { await ftp.Disconnect(ct); }
        }
        return new {success=true,remoteDirectory=directory,message="Conexão e escrita verificadas. Arquivo de teste removido."};
    }
}
