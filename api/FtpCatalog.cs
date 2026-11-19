using FluentFTP;
using Microsoft.Net.Http.Headers;

public sealed class FtpCatalog(StorageService storage, Database db)
{
    private AsyncFtpClient ConnectClient(Storage s)
    {
        var ftp = new AsyncFtpClient(s.Host,s.Username,storage.Password(s),s.Port);
        ftp.Config.ConnectTimeout=10000; ftp.Config.ReadTimeout=30000;
        ftp.Config.DataConnectionConnectTimeout=10000; ftp.Config.DataConnectionReadTimeout=60000;
        ftp.Config.DataConnectionType=FtpDataConnectionType.EPSV;
        ftp.Config.TimeConversion=FtpDate.UTC;
        return ftp;
    }

    public async Task<IResult> List(Guid id, CancellationToken ct)
    {
        var clients=await db.Query("SELECT name,slug FROM clients WHERE id=$1",id);
        if(clients.Count==0) return Results.NotFound();
        var s=await storage.Get() ?? throw new ArgumentException("Configure o destino FTP primeiro.");
        var directory=s.BaseDirectory+"/"+clients[0]["slug"];
        using var ftp=ConnectClient(s);
        await ftp.Connect(ct);
        var exists=await ftp.DirectoryExists(directory,ct);
        var listing=exists ? await ftp.GetListing(directory,FtpListOption.Modify | FtpListOption.Size,ct) : [];
        var runs=await db.Query("SELECT file_name,backup_type,started_at FROM runs WHERE client_id=$1 AND status='Succeeded' ORDER BY started_at DESC",id);
        var history=runs.GroupBy(r=>(string)r["file_name"]!,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.Ordinal);
        var files=listing.Where(f=>f.Type==FtpObjectType.File && BackupFileMetadata.SafeName(f.Name)
            && (BackupFileMetadata.CanDownload(f.Name)||f.Name.EndsWith(".zip.uploading",StringComparison.OrdinalIgnoreCase)))
            .Select(f=>{
                var metadata=BackupFileMetadata.Parse(f.Name);
                history.TryGetValue(f.Name,out var run);
                var createdAt=run is null ? metadata.CreatedAt : new DateTimeOffset(DateTime.SpecifyKind((DateTime)run["started_at"]!,DateTimeKind.Utc));
                return new {fileName=f.Name,sizeBytes=f.Size<0?(long?)null:f.Size,backupType=run?["backup_type"]??metadata.Type,
                    backupAt=createdAt,dateSource=run is not null?"Execution":metadata.CreatedAt.HasValue?"FileName":"Unknown",
                    modifiedAt=f.Modified==DateTime.MinValue?(DateTimeOffset?)null:new DateTimeOffset(DateTime.SpecifyKind(f.Modified,DateTimeKind.Utc)),
                    status=BackupFileMetadata.CanDownload(f.Name)?"Available":"Uploading",hasHistory=run is not null};
            }).OrderByDescending(f=>f.backupAt??f.modifiedAt).ThenBy(f=>f.fileName).ToArray();
        return Results.Ok(new {clientId=id,clientName=clients[0]["name"],remoteDirectory=directory,directoryExists=exists,checkedAt=DateTimeOffset.UtcNow,files});
    }

    public async Task Download(Guid id, string fileName, HttpContext ctx)
    {
        if(!BackupFileMetadata.CanDownload(fileName)) throw new ArgumentException("Selecione um arquivo ZIP final, sem diretórios.");
        var clients=await db.Query("SELECT slug FROM clients WHERE id=$1",id);
        if(clients.Count==0){ctx.Response.StatusCode=404;return;}
        var s=await storage.Get() ?? throw new ArgumentException("Configure o destino FTP primeiro.");
        var directory=s.BaseDirectory+"/"+clients[0]["slug"];
        using var ftp=ConnectClient(s);
        await ftp.Connect(ctx.RequestAborted);
        if(!await ftp.DirectoryExists(directory,ctx.RequestAborted)){ctx.Response.StatusCode=404;return;}
        // Resolve only a direct regular file in this client's directory; never follow links or accept a supplied path.
        var entry=(await ftp.GetListing(directory,FtpListOption.Size,ctx.RequestAborted))
            .FirstOrDefault(f=>f.Type==FtpObjectType.File && f.Name==fileName);
        if(entry is null){ctx.Response.StatusCode=404;return;}
        await using var input=await ftp.OpenRead(directory+"/"+fileName,FtpDataType.Binary,0,false,ctx.RequestAborted);
        ctx.Response.ContentType="application/zip";
        ctx.Response.Headers.CacheControl="no-store";
        var disposition=new ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(fileName);
        ctx.Response.Headers.ContentDisposition=disposition.ToString();
        if(entry.Size>=0) ctx.Response.ContentLength=entry.Size;
        await db.Execute("INSERT INTO audit(action,detail) VALUES('backup.download',$1)",id+":"+fileName);
        await input.CopyToAsync(ctx.Response.Body,65536,ctx.RequestAborted);
    }
}
