using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

public record RegistrationInput(Guid RegistrationId,Guid InstanceId,string DeviceSecret,string MachineName,string RequestedName,string? ExistingAgentToken=null);
public record RegistrationApproval(Guid? ClientId=null,ClientInput? Client=null,string? ConfirmationCode=null);
public static class RegistrationPolicy
{
    public static void Validate(RegistrationInput input)
    {
        static bool Name(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=120&&!value.Any(char.IsControl);
        if(input.RegistrationId==Guid.Empty||input.InstanceId==Guid.Empty||input.DeviceSecret is null||!Regex.IsMatch(input.DeviceSecret,"^[a-fA-F0-9]{64}$")||!Name(input.MachineName)||!Name(input.RequestedName)||input.ExistingAgentToken is not null&&(input.ExistingAgentToken.Length is <32 or >200||input.ExistingAgentToken.Any(char.IsControl)))throw new ArgumentException("Cadastro de máquina inválido.");
    }
    public static void ValidateClient(ClientInput c)
    {
        if(string.IsNullOrWhiteSpace(c.Name)||c.Name.Length>120||c.Name.Any(char.IsControl)||c.Slug is null||!Regex.IsMatch(c.Slug,"^[a-z0-9][a-z0-9_-]{1,62}$")||string.IsNullOrWhiteSpace(c.Database)||c.Database.Length>128||c.Database.Any(char.IsControl)||c.ExpectedIntervalHours is <1 or >8760||c.GraceMinutes is <0 or >10080||c.DifferentialIntervalHours<0||c.DifferentialIntervalHours>=c.ExpectedIntervalHours||!c.Enabled)throw new ArgumentException("Informe nome, identificador, banco e agenda válidos para um cliente ativo.");
    }
    public static string NormalizeCode(string? code)=>Regex.Replace(code??"","[-\\s]","").ToUpperInvariant();
}

public sealed class RegistrationService(NpgsqlDataSource source,IDataProtectionProvider protection,InstallationService installation)
{
    private readonly IDataProtector protector=protection.CreateProtector("BackupControl.Installation.v1");
    private static string Hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Same(string? left,string? right)=>left is not null&&right is not null&&CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left),Encoding.ASCII.GetBytes(right));
    private static string Token()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static NpgsqlCommand Cmd(NpgsqlConnection c,string sql,params object?[] args){var q=new NpgsqlCommand(sql,c);foreach(var value in args)q.Parameters.Add(new NpgsqlParameter{Value=value??DBNull.Value});return q;}
    private sealed record Registration(Guid Id,Guid Instance,string SecretHash,string Code,string Machine,string Name,string? ExistingHash,string Origin,string Status,DateTime Expires,Guid? ClientId,Guid? EnrollmentId);
    private static async Task<Registration?> Read(NpgsqlConnection c,Guid id,bool locked=false)
    {
        await using var q=Cmd(c,"SELECT id,instance_id,secret_hash,code,machine_name,requested_name,existing_token_hash,central_url,status,expires_at,client_id,enrollment_id FROM machine_registrations WHERE id=$1"+(locked?" FOR UPDATE":""),id);
        await using var r=await q.ExecuteReaderAsync();if(!await r.ReadAsync())return null;
        return new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.GetString(7),r.GetString(8),r.GetDateTime(9),r.IsDBNull(10)?null:r.GetGuid(10),r.IsDBNull(11)?null:r.GetGuid(11));
    }
    public async Task<IResult> Register(RegistrationInput input,HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl="no-store";RegistrationPolicy.Validate(input);
        var settings=await installation.Settings();if(settings is null)return Results.Conflict(new{error="A central ainda não possui endereço HTTPS configurado."});
        var code=Convert.ToHexString(RandomNumberGenerator.GetBytes(5));code=code[..5]+"-"+code[5..];
        await using(var c=await source.OpenConnectionAsync()){
            await using var tx=await c.BeginTransactionAsync();
            await Cmd(c,"INSERT INTO machine_registrations(id,instance_id,secret_hash,code,machine_name,requested_name,existing_token_hash,central_url,expires_at) VALUES($1,$2,$3,$4,$5,$6,$7,$8,now()+interval '15 minutes') ON CONFLICT(id) DO NOTHING",input.RegistrationId,input.InstanceId,Hash(input.DeviceSecret),code,input.MachineName.Trim(),input.RequestedName.Trim(),input.ExistingAgentToken is null?null:Hash(input.ExistingAgentToken),settings.CentralUrl).ExecuteNonQueryAsync();
            var row=await Read(c,input.RegistrationId,true);
            if(row is null||!Same(row.SecretHash,Hash(input.DeviceSecret))||row.Instance!=input.InstanceId||row.Machine!=input.MachineName.Trim()||row.Name!=input.RequestedName.Trim()||row.ExistingHash!=(input.ExistingAgentToken is null?null:Hash(input.ExistingAgentToken)))return Results.Conflict(new{error="Este cadastro pertence a outra solicitação ou teve seus dados alterados."});
            await tx.CommitAsync();
        }
        return await Poll(input.RegistrationId,input.DeviceSecret,ctx);
    }
    public async Task<IResult> Poll(Guid id,string? secret,HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl="no-store";
        if(secret is null||!Regex.IsMatch(secret,"^[a-fA-F0-9]{64}$"))return Results.Unauthorized();
        await using var c=await source.OpenConnectionAsync();var row=await Read(c,id);if(row is null||!Same(row.SecretHash,Hash(secret)))return Results.Unauthorized();
        var status=row.Status;string? key=null;string? clientName=null;
        if(row.Expires<=DateTime.UtcNow)status="Expired";
        if(status=="Approved"){
            await using var q=Cmd(c,"SELECT e.protected_key,c.name,e.revoked OR e.expires_at<=now(),c.enabled FROM installer_enrollments e JOIN clients c ON c.id=e.client_id WHERE e.id=$1 AND e.client_id=$2",row.EnrollmentId,row.ClientId);
            await using var r=await q.ExecuteReaderAsync();
            if(!await r.ReadAsync()||r.GetBoolean(2))status="Expired";
            else if(!r.GetBoolean(3))status="Rejected";
            else{key=protector.Unprotect(r.GetString(0));clientName=r.GetString(1);}
        }
        if(status=="Approved")return Results.Ok(new{registrationId=row.Id,status,code=row.Code,expiresAt=row.Expires,machineName=row.Machine,requestedName=row.Name,clientId=row.ClientId,clientName,centralUrl=row.Origin,enrollmentKey=key});
        return Results.Ok(new{registrationId=row.Id,status,code=row.Code,expiresAt=row.Expires,machineName=row.Machine,requestedName=row.Name});
    }
    public async Task<IResult> List(Database db)=>Results.Ok(await db.Query("""
      SELECT r.id AS "registrationId",r.instance_id AS "instanceId",r.machine_name AS "machineName",r.requested_name AS "requestedName",
      CASE WHEN r.expires_at<=now() THEN 'Expired' ELSE r.status END AS status,r.expires_at AS "expiresAt",r.created_at AS "createdAt",r.client_id AS "clientId",
      (SELECT c.id FROM clients c WHERE c.enabled AND c.agent_instance_id=r.instance_id AND c.token_hash=r.existing_token_hash LIMIT 1) AS "existingClientId"
      FROM machine_registrations r WHERE r.created_at>now()-interval '7 days' ORDER BY r.created_at DESC LIMIT 200
      """));
    public async Task<IResult> Approve(Guid id,RegistrationApproval input)
    {
        if(input.ClientId.HasValue==(input.Client is not null))throw new ArgumentException("Selecione um cliente existente ou informe um novo cliente.");
        if(input.Client is not null)RegistrationPolicy.ValidateClient(input.Client);
        var settings=await installation.Settings();if(string.IsNullOrEmpty(settings?.SqlPassword)||await installation.CurrentDigest() is null||!File.Exists(installation.Template))return Results.Conflict(new{error="Configure SQL e publique o pacote do agente antes de aprovar."});
        await using var c=await source.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();var row=await Read(c,id,true);if(row is null)return Results.NotFound();
        if(RegistrationPolicy.NormalizeCode(input.ConfirmationCode)!=RegistrationPolicy.NormalizeCode(row.Code))return Results.BadRequest(new{error="Confirme o código exibido na máquina antes de aprovar."});
        if(row.Expires<=DateTime.UtcNow)return Results.Conflict(new{error="Cadastro expirado. Solicite um novo cadastro na máquina."});
        if(row.Status=="Approved"){
            if(input.ClientId.HasValue&&input.ClientId!=row.ClientId)return Results.Conflict(new{error="Cadastro já aprovado para outro cliente."});
            await tx.CommitAsync();return Results.Ok(new{registrationId=id,status="Approved",clientId=row.ClientId});
        }
        if(row.Status!="Pending")return Results.Conflict(new{error="Este cadastro não está aguardando aprovação."});
        var clientId=input.ClientId??Guid.NewGuid();
        if(input.Client is {} fresh){
            await Cmd(c,"INSERT INTO clients(id,name,slug,database,interval_hours,grace_minutes,enabled,token_hash,differential_interval_hours) VALUES($1,$2,$3,$4,$5,$6,true,$7,$8)",clientId,fresh.Name.Trim(),fresh.Slug,fresh.Database.Trim(),fresh.ExpectedIntervalHours,fresh.GraceMinutes,Hash(Token()),fresh.DifferentialIntervalHours).ExecuteNonQueryAsync();
        }
        await using(var q=Cmd(c,"SELECT agent_instance_id,token_hash,enabled FROM clients WHERE id=$1 FOR UPDATE",clientId)){
            await using var r=await q.ExecuteReaderAsync();if(!await r.ReadAsync())return Results.NotFound();if(!r.GetBoolean(2))return Results.Conflict(new{error="O cliente está desativado."});
            if(!r.IsDBNull(0)&&(r.GetGuid(0)!=row.Instance||!Same(row.ExistingHash,r.GetString(1))))return Results.Conflict(new{error="Cliente já vinculado. O reparo exige a identidade e a credencial da instalação existente."});
        }
        var key=Token();var enrollment=Guid.NewGuid();var expires=DateTime.UtcNow.AddHours(24);
        await Cmd(c,"UPDATE clients SET agent_instance_id=$2 WHERE id=$1",clientId,row.Instance).ExecuteNonQueryAsync();
        await Cmd(c,"INSERT INTO installer_enrollments(id,client_id,key_hash,protected_key,central_url,expires_at) VALUES($1,$2,$3,$4,$5,$6)",enrollment,clientId,Hash(key),protector.Protect(key),row.Origin,expires).ExecuteNonQueryAsync();
        await Cmd(c,"UPDATE machine_registrations SET status='Approved',client_id=$2,enrollment_id=$3,decided_at=now(),expires_at=$4 WHERE id=$1",id,clientId,enrollment,expires).ExecuteNonQueryAsync();
        await Cmd(c,"INSERT INTO audit(action,detail) VALUES('machine.approved',$1)",id+":"+clientId).ExecuteNonQueryAsync();
        if(input.Client is not null)await Cmd(c,"INSERT INTO audit(action,detail) VALUES('client.created',$1)",input.Client.Slug).ExecuteNonQueryAsync();
        await tx.CommitAsync();return Results.Ok(new{registrationId=id,status="Approved",clientId});
    }
    public async Task<IResult> Reject(Guid id,Database db)
    {
        var rows=await db.Query("UPDATE machine_registrations SET status='Rejected',decided_at=now() WHERE id=$1 AND status='Pending' AND expires_at>now() RETURNING id",id);
        if(rows.Count==0)return Results.Conflict(new{error="Somente cadastros pendentes podem ser recusados."});
        await db.Execute("INSERT INTO audit(action,detail) VALUES('machine.rejected',$1)",id.ToString());return Results.NoContent();
    }
    public async Task Download(HttpContext ctx)
    {
        var settings=await installation.Settings();if(settings is null||!File.Exists(installation.Template)){ctx.Response.StatusCode=503;return;}
        var payload=JsonSerializer.SerializeToUtf8Bytes(new{centralUrl=InstallationService.Origin(settings.CentralUrl)});
        await using var file=File.OpenRead(installation.Template);ctx.Response.ContentType="application/octet-stream";ctx.Response.Headers.ContentDisposition="attachment; filename=BackupControl.Setup.exe";ctx.Response.Headers.CacheControl="no-store";ctx.Response.ContentLength=file.Length+payload.Length+16;
        await file.CopyToAsync(ctx.Response.Body,ctx.RequestAborted);await ctx.Response.Body.WriteAsync(payload,ctx.RequestAborted);var size=new byte[8];BinaryPrimitives.WriteInt64LittleEndian(size,payload.Length);await ctx.Response.Body.WriteAsync(size,ctx.RequestAborted);await ctx.Response.Body.WriteAsync("BCGEN001"u8.ToArray(),ctx.RequestAborted);
    }
}
