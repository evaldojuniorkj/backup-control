using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

public record UpdateInput(Guid[] ClientIds);
public record UpdateStatusInput(string State,string? Message);
public record AgentRelease(string? Version,string? Sha256,bool Available);

public static class UpdatePolicy
{
    public static bool IsUpgrade(string? installed,string? target)=>Version.TryParse(installed,out var a)&&Version.TryParse(target,out var b)&&b>a;
    public static bool Confirmed(string target,string? installed,DateTimeOffset? seen,DateTimeOffset requested,DateTimeOffset now)=>
        target==installed && seen>=requested && seen>=now.AddMinutes(-5) && seen<=now.AddMinutes(1);
    public static bool CanTransition(string from,string to)=>from==to || (from,to) switch
    {
        ("Pending","Waiting" or "Downloading" or "Failed")=>true,
        ("Waiting","Downloading" or "Failed")=>true,
        // A protected recovery journal can restore files after a crash before Applying reached the central.
        ("Downloading","Waiting" or "Applying" or "Failed" or "RolledBack")=>true,
        ("Applying","Verifying" or "Succeeded" or "Failed" or "RolledBack")=>true,
        ("Verifying","Succeeded" or "Failed" or "RolledBack")=>true,
        _=>false
    };
    // Worker messages may include exception strings containing operational secrets. Persist only fixed diagnostics.
    public static string Message(string state)=>state switch {
        "Waiting"=>"Aguardando o término do backup e confirmação dos resultados.",
        "Downloading"=>"Baixando e validando o pacote assinado.","Applying"=>"Aplicando atualização do agente.",
        "Verifying"=>"Aguardando heartbeat da versão instalada.","Succeeded"=>"Versão instalada confirmada pela central.",
        "Failed"=>"Atualização não concluída; consulte o diagnóstico local protegido.",
        "RolledBack"=>"Falha na atualização; a versão anterior foi restaurada.","Cancelled"=>"Atualização cancelada pela equipe.",_=>"Atualização solicitada."
    };
}

public sealed class UpdateService(NpgsqlDataSource source,InstallationService installation)
{
    const string Active="('Pending','Waiting','Downloading','Applying','Verifying')";
    internal static NpgsqlCommand Cmd(NpgsqlConnection connection,string sql,params object?[] args)
    {
        var command=new NpgsqlCommand(sql,connection);
        foreach(var arg in args)command.Parameters.Add(new NpgsqlParameter{Value=arg??DBNull.Value});
        return command;
    }
    public async Task<AgentRelease> Release()
    {
        try {
            var metadata=JsonSerializer.Deserialize<AgentRelease>(await File.ReadAllTextAsync(Path.Combine(installation.Distribution,"release.json")),new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if(metadata is null || !Version.TryParse(metadata.Version,out _) || metadata.Version?.Length>40 || metadata.Sha256 is null || installation.SnapshotPath(metadata.Sha256) is not string path || !File.Exists(path))return new(null,null,false);
            if(await installation.CurrentDigest()!=metadata.Sha256)return new(metadata.Version,metadata.Sha256,false);
            await using var stream=File.OpenRead(path);
            var actual=Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            return new(metadata.Version,metadata.Sha256,actual==metadata.Sha256);
        } catch(IOException){return new(null,null,false);}catch(JsonException){return new(null,null,false);}
    }
    public async Task<IResult> List(Database db)=>Results.Ok(new{release=await Release(),jobs=await db.Query("SELECT u.id,u.client_id AS \"clientId\",c.name AS \"clientName\",u.state,u.target_version AS \"targetVersion\",u.requested_at AS \"requestedAt\",u.updated_at AS \"updatedAt\",u.message,c.agent_version AS \"installedVersion\" FROM agent_updates u JOIN clients c ON c.id=u.client_id ORDER BY u.requested_at DESC LIMIT 300")});

    public async Task<IResult> Queue(UpdateInput input)
    {
        if(input.ClientIds is null || input.ClientIds.Length is <1 or >500 || input.ClientIds.Any(x=>x==Guid.Empty))throw new ArgumentException("Selecione de 1 a 500 clientes.");
        var release=await Release();if(!release.Available)return Results.Json(new{error="Pacote de atualização indisponível ou inválido."},statusCode:503);
        var accepted=new List<object>();var rejected=new List<object>();
        await using var connection=await source.OpenConnectionAsync();await using var tx=await connection.BeginTransactionAsync();
        // Deterministic lock order also prevents deadlocks for overlapping batch requests.
        foreach(var id in input.ClientIds.Distinct().Order()) {
            string? reason=null;
            await using(var r=await Cmd(connection,"SELECT enabled,agent_instance_id,remote_update_supported,agent_version,deletion_pending,deactivation_confirmed_at FROM clients WHERE id=$1 FOR UPDATE",id).ExecuteReaderAsync()) {
                if(!await r.ReadAsync())reason="Cliente não encontrado.";
                else if((!r.GetBoolean(0)&&!r.GetBoolean(4))||!r.IsDBNull(5))reason="Cliente pausado ou desativação confirmada.";
                else if(r.IsDBNull(1)||!r.GetBoolean(2))reason="Reinstale o agente para habilitar a atualização remota.";
                else if(!UpdatePolicy.IsUpgrade(r.IsDBNull(3)?null:r.GetString(3),release.Version))reason="A versão instalada não é anterior à versão publicada.";
            }
            if(reason is null && await Cmd(connection,$"SELECT id FROM agent_updates WHERE client_id=$1 AND state IN {Active}",id).ExecuteScalarAsync() is not null)reason="Já existe uma atualização ativa.";
            if(reason is not null){rejected.Add(new{clientId=id,error=reason});continue;}
            var job=Guid.NewGuid();
            await Cmd(connection,"INSERT INTO agent_updates(id,client_id,target_version,sha256,message) VALUES($1,$2,$3,$4,$5)",job,id,release.Version,release.Sha256,UpdatePolicy.Message("Pending")).ExecuteNonQueryAsync();
            await Cmd(connection,"INSERT INTO audit(action,detail) VALUES('agent.update.requested',$1)",id+":"+job+":"+release.Version).ExecuteNonQueryAsync();
            accepted.Add(new{id=job,clientId=id,state="Pending"});
        }
        await tx.CommitAsync();return Results.Ok(new{accepted,rejected});
    }
    public async Task<IResult> Cancel(Guid id,Database db)
    {
        var rows=await db.Query("UPDATE agent_updates SET state='Cancelled',message=$2,updated_at=now() WHERE id=$1 AND state IN ('Pending','Waiting') RETURNING id",id,UpdatePolicy.Message("Cancelled"));
        return rows.Count==0?Results.Conflict(new{error="Somente atualizações pendentes ou em espera podem ser canceladas."}):Results.NoContent();
    }
    public async Task Reconcile(Guid cid,Database db)=>await db.Execute("UPDATE agent_updates u SET state='Succeeded',updated_at=now(),message=$2 FROM clients c WHERE u.client_id=$1 AND c.id=u.client_id AND u.state='Verifying' AND c.agent_version=u.target_version AND c.last_seen>=u.requested_at AND c.last_seen>=now()-interval '5 minutes'",cid,UpdatePolicy.Message("Succeeded"));

    public async Task<IResult> Current(Guid cid,Database db)
    {
        await Reconcile(cid,db);
        var rows=await db.Query($"SELECT id,target_version AS \"targetVersion\",sha256,state FROM agent_updates WHERE client_id=$1 AND state IN {Active} ORDER BY requested_at LIMIT 1",cid);
        if(rows.Count==0)return Results.NoContent();
        var settings=await installation.Settings();
        if(settings is null)return Results.Json(new{error="Origem HTTPS da central não configurada."},statusCode:503);
        var row=rows[0];row["url"]=InstallationService.Origin(settings.CentralUrl)+"/api/agent/package/"+row["sha256"];
        return Results.Ok(row);
    }
    public async Task<IResult> Status(Guid cid,Guid id,UpdateStatusInput input)
    {
        if(input.State is not ("Waiting" or "Downloading" or "Applying" or "Verifying" or "Succeeded" or "Failed" or "RolledBack") || input.Message?.Length>4000)throw new ArgumentException("Estado ou diagnóstico de atualização inválido.");
        await using var connection=await source.OpenConnectionAsync();await using var tx=await connection.BeginTransactionAsync();
        // Same lock ordering as deletion and queue operations.
        await Cmd(connection,"SELECT id FROM clients WHERE id=$1 FOR UPDATE",cid).ExecuteScalarAsync();
        string previous,target;DateTimeOffset requested;DateTimeOffset? seen;string? installed;
        await using(var r=await Cmd(connection,"SELECT u.state,u.target_version,u.requested_at,c.last_seen,c.agent_version FROM agent_updates u JOIN clients c ON c.id=u.client_id WHERE u.id=$1 AND u.client_id=$2 FOR UPDATE OF u",id,cid).ExecuteReaderAsync()) {
            if(!await r.ReadAsync())return Results.NotFound();
            previous=r.GetString(0);target=r.GetString(1);requested=r.GetFieldValue<DateTimeOffset>(2);seen=r.IsDBNull(3)?null:r.GetFieldValue<DateTimeOffset>(3);installed=r.IsDBNull(4)?null:r.GetString(4);
        }
        if(!UpdatePolicy.CanTransition(previous,input.State))return Results.Conflict(new{error="Transição de atualização inválida."});
        if(previous is "Succeeded" or "Failed" or "RolledBack" or "Cancelled")return Results.Ok(new{state=previous});
        var state=input.State=="Succeeded"&&!UpdatePolicy.Confirmed(target,installed,seen,requested,DateTimeOffset.UtcNow)?"Verifying":input.State;
        await Cmd(connection,"UPDATE agent_updates SET state=$2,message=$3,updated_at=now() WHERE id=$1",id,state,UpdatePolicy.Message(state)).ExecuteNonQueryAsync();
        await tx.CommitAsync();return Results.Ok(new{state});
    }
}
