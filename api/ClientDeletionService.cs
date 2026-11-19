using FluentFTP;
using Npgsql;
using System.Text.Json;
using System.Text.RegularExpressions;

public record ClientDeletionInput(string ConfirmationSlug,bool AgentStopped=false);
public enum DeletionEntryKind { File,Directory,Link }
public record DeletionEntry(string Name,DeletionEntryKind Kind);
public record DeletionCounts(int Files,int Directories);
public interface IDeletionFtp
{
    Task<DeletionEntry[]> List(string path,CancellationToken ct);
    Task DeleteFile(string path,CancellationToken ct);
    Task RemoveDirectory(string path,CancellationToken ct);
}
public interface IClientDirectoryDeletion
{
    Task<DeletionCounts> Delete(Storage storage,string slug,CancellationToken ct);
}
public static class ClientDeletionPolicy
{
    public static string Path(string root,string slug)
    {
        if(!Regex.IsMatch(root,"^(/[a-zA-Z0-9_-]+)+/?$") || !Regex.IsMatch(slug,"^[a-z0-9][a-z0-9_-]{1,62}$"))throw new ArgumentException("Diretório ou identificador inválido para exclusão segura.");
        return root.TrimEnd('/')+"/"+slug;
    }
    public static async Task<DeletionCounts> Remove(IDeletionFtp ftp,string root,string slug,CancellationToken ct)
    {
        var target=Path(root,slug);var cursor="/";
        // Resolve every ancestor from /; DirectoryExists alone can silently follow a symbolic link.
        foreach(var segment in target.Split('/',StringSplitOptions.RemoveEmptyEntries)) {
            var matches=(await ftp.List(cursor,ct)).Where(x=>x.Name==segment).ToArray();
            if(matches.Length==0)return new(0,0);
            if(matches.Length!=1||matches[0].Kind!=DeletionEntryKind.Directory)throw new IOException("A pasta ou um ancestral é um link ou não é diretório.");
            cursor=cursor.TrimEnd('/')+"/"+segment;
        }
        var plan=new Dictionary<string,DeletionEntry[]>(StringComparer.Ordinal);var entries=0;
        async Task Scan(string directory,int depth) {
            if(depth>32||plan.Count>=10000)throw new IOException("Árvore FTP excede os limites de exclusão segura.");
            var listing=await ftp.List(directory,ct);
            if((entries+=listing.Length)>100000 || listing.Select(x=>x.Name).Distinct(StringComparer.Ordinal).Count()!=listing.Length || listing.Any(x=>!FtpTreePolicy.SafeName(x.Name)||x.Kind==DeletionEntryKind.Link))throw new IOException("Árvore FTP contém link, nome inseguro ou excede os limites.");
            plan.Add(directory,listing);
            foreach(var child in listing.Where(x=>x.Kind==DeletionEntryKind.Directory))await Scan(directory+"/"+child.Name,depth+1);
        }
        await Scan(target,0); // No mutation until the whole tree has passed link and scope validation.
        var files=0;var directories=0;
        async Task RemoveTree(string directory) {
            var expected=plan[directory];var actual=await ftp.List(directory,ct);
            if(!actual.OrderBy(x=>x.Name,StringComparer.Ordinal).SequenceEqual(expected.OrderBy(x=>x.Name,StringComparer.Ordinal)))throw new IOException("A pasta FTP mudou durante a exclusão. Mantenha o agente parado e tente novamente.");
            foreach(var item in expected) {
                var path=directory+"/"+item.Name;
                if(item.Kind==DeletionEntryKind.Directory)await RemoveTree(path);
                else {await ftp.DeleteFile(path,ct);files++;}
            }
            // Use a single RMD, never the FTP library's recursive deletion (which may follow links).
            await ftp.RemoveDirectory(directory,ct);directories++;
        }
        await RemoveTree(target);return new(files,directories);
    }
}
public sealed class FtpClientDirectoryDeletion(StorageService storageService):IClientDirectoryDeletion
{
    sealed class Adapter(AsyncFtpClient ftp):IDeletionFtp
    {
        public async Task<DeletionEntry[]> List(string path,CancellationToken ct)=>(await ftp.GetListing(path,FtpListOption.Size,ct)).Select(x=>new DeletionEntry(x.Name,x.Type==FtpObjectType.File?DeletionEntryKind.File:x.Type==FtpObjectType.Directory?DeletionEntryKind.Directory:DeletionEntryKind.Link)).ToArray();
        public Task DeleteFile(string path,CancellationToken ct)=>ftp.DeleteFile(path,ct);
        public async Task RemoveDirectory(string path,CancellationToken ct){var reply=await ftp.Execute("RMD "+path,ct);if(!reply.Success)throw new IOException("FTP não confirmou remoção da pasta.");}
    }
    public async Task<DeletionCounts> Delete(Storage storage,string slug,CancellationToken ct)
    {
        ClientDeletionPolicy.Path(storage.BaseDirectory,slug);
        using var ftp=new AsyncFtpClient(storage.Host,storage.Username,storageService.Password(storage),storage.Port);
        ftp.Config.ConnectTimeout=10000;ftp.Config.ReadTimeout=20000;ftp.Config.DataConnectionConnectTimeout=10000;ftp.Config.DataConnectionReadTimeout=20000;ftp.Config.DataConnectionType=FtpDataConnectionType.EPSV;
        await ftp.Connect(ct);
        return await ClientDeletionPolicy.Remove(new Adapter(ftp),storage.BaseDirectory,slug,ct);
    }
}
public sealed class ClientDeletionService(NpgsqlDataSource source,StorageService storageService,IClientDirectoryDeletion deletion)
{
    public async Task<IResult> Delete(Guid id,ClientDeletionInput input,CancellationToken ct)
    {
        string slug;bool installed;
        await using var connection=await source.OpenConnectionAsync(ct);
        await using(var tx=await connection.BeginTransactionAsync(ct)) {
            await using(var r=await UpdateService.Cmd(connection,"SELECT slug,enabled,agent_instance_id IS NOT NULL OR last_seen IS NOT NULL OR EXISTS(SELECT 1 FROM installer_enrollments e WHERE e.client_id=clients.id AND e.redeemed_at IS NOT NULL) FROM clients WHERE id=$1 FOR UPDATE",id).ExecuteReaderAsync(ct)) {
                if(!await r.ReadAsync(ct))return Results.NotFound();slug=r.GetString(0);
                if(r.GetBoolean(1))return Results.Conflict(new{error="Desative o cliente antes de excluir definitivamente seus dados e backups."});
                if(slug!=input.ConfirmationSlug)throw new ArgumentException("Digite o identificador exato do cliente para confirmar a exclusão.");
                installed=r.GetBoolean(2);
            }
            await UpdateService.Cmd(connection,"UPDATE clients SET enabled=false,deletion_pending=true,deactivation_id=COALESCE(deactivation_id,gen_random_uuid()),deactivation_requested_at=COALESCE(deactivation_requested_at,now()) WHERE id=$1",id).ExecuteNonQueryAsync(ct);
            await UpdateService.Cmd(connection,"UPDATE commands SET state='Cancelled',finished_at=now() WHERE client_id=$1 AND state='Pending'",id).ExecuteNonQueryAsync(ct);
            await UpdateService.Cmd(connection,"UPDATE agent_updates SET state='Cancelled',updated_at=now(),message='Nova tentativa de desativação solicitada.' WHERE client_id=$1 AND state IN ('Failed','RolledBack')",id).ExecuteNonQueryAsync(ct);
            await UpdateService.Cmd(connection,"UPDATE installer_enrollments SET revoked=true WHERE client_id=$1",id).ExecuteNonQueryAsync(ct);
            await UpdateService.Cmd(connection,"UPDATE machine_registrations SET status='Rejected',decided_at=now() WHERE client_id=$1 AND status='Pending'",id).ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
        }
        if(installed) {
            var rows=await new Database(source).Query("SELECT "+DeactivationService.StateSql+" AS state FROM clients c WHERE id=$1",id);
            if(rows.Count==0)return Results.NoContent();
            return Results.Json(new{pending=true,state=rows[0]["state"],message="Aguardando desativação permanente confirmada pelo agente."},statusCode:202);
        }
        return await Finalize(id,ct);
    }
    public async Task<IResult> Finalize(Guid id,CancellationToken ct)
    {
        var storage=await storageService.Get();if(storage is null)return Results.StatusCode(503);
        await using var connection=await source.OpenConnectionAsync(ct);
        await using var finalTx=await connection.BeginTransactionAsync(ct);
        // Serialize retries, with the row lock also excluding queue/status changes during FTP deletion.
        string slug;
        await using(var r=await UpdateService.Cmd(connection,"SELECT slug,deactivation_confirmed_at IS NOT NULL OR (agent_instance_id IS NULL AND last_seen IS NULL AND NOT EXISTS(SELECT 1 FROM installer_enrollments e WHERE e.client_id=clients.id AND e.redeemed_at IS NOT NULL)) FROM clients WHERE id=$1 AND deletion_pending AND NOT enabled FOR UPDATE",id).ExecuteReaderAsync(ct)) {
            if(!await r.ReadAsync(ct))return Results.NotFound();slug=r.GetString(0);
            if(!r.GetBoolean(1))return Results.Conflict();
        }
        DeletionCounts counts;
        try {counts=await deletion.Delete(storage,slug,ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception) {
            await finalTx.RollbackAsync(CancellationToken.None);
            return Results.Json(new{error="Falha ao remover a pasta FTP. O cliente continua pausado e o histórico foi preservado. Mantenha o agente parado e tente novamente.",paused=true,remoteDirectory=ClientDeletionPolicy.Path(storage.BaseDirectory,slug)},statusCode:502);
        }
        var runs=Convert.ToInt64(await UpdateService.Cmd(connection,"SELECT count(*) FROM runs WHERE client_id=$1",id).ExecuteScalarAsync(ct));
        var commands=Convert.ToInt64(await UpdateService.Cmd(connection,"SELECT count(*) FROM commands WHERE client_id=$1",id).ExecuteScalarAsync(ct));
        // Explicit order avoids runs.command_id NO ACTION constraints during cascading client removal.
        await UpdateService.Cmd(connection,"DELETE FROM runs WHERE client_id=$1",id).ExecuteNonQueryAsync(ct);
        await UpdateService.Cmd(connection,"DELETE FROM machine_registrations WHERE client_id=$1",id).ExecuteNonQueryAsync(ct);
        await UpdateService.Cmd(connection,"DELETE FROM clients WHERE id=$1",id).ExecuteNonQueryAsync(ct);
        await UpdateService.Cmd(connection,"INSERT INTO audit(action,detail) VALUES('client.deleted',$1)",JsonSerializer.Serialize(new{clientId=id,slug,remoteDirectory=ClientDeletionPolicy.Path(storage.BaseDirectory,slug),files=counts.Files,directories=counts.Directories,runs,commands})).ExecuteNonQueryAsync(ct);
        await finalTx.CommitAsync(ct);return Results.NoContent();
    }
}
