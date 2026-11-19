using Npgsql;

public sealed class DeactivationService(NpgsqlDataSource source,UpdateService updates)
{
    public static bool LegacyReportDrain(Dictionary<string,object?> row)=>
        row.TryGetValue("enabled",out var enabled)&&enabled is false&&row["deletion_pending"] is true&&row["deactivation_confirmed_at"] is null&&
        row["remote_update_supported"] is true&&row["agent_version"] is string version&&Version.TryParse(version,out var installed)&&installed>=new Version(0,8,0)&&installed<new Version(0,9,0);
    public const string StateSql="CASE WHEN c.deactivation_requested_at IS NULL THEN NULL WHEN c.deactivation_confirmed_at IS NOT NULL THEN 'Confirmed' WHEN c.remote_deactivation_supported OR c.agent_version ~ '^([1-9][0-9]*[.]|0[.]([9]|[1-9][0-9]+)[.])' OR c.remote_update_supported THEN 'WaitingForAgent' ELSE 'RequiresUpgrade' END";
    public async Task EnsureUpgrade(Guid cid)
    {
        var release=await updates.Release();if(!release.Available||!Version.TryParse(release.Version,out var v)||v<new Version(0,9,0))return;
        await using var c=await source.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();
        string? installed;bool eligible;
        await using(var r=await UpdateService.Cmd(c,"SELECT agent_version,deletion_pending AND deactivation_confirmed_at IS NULL AND remote_update_supported AND agent_instance_id IS NOT NULL FROM clients WHERE id=$1 FOR UPDATE",cid).ExecuteReaderAsync()) {
            if(!await r.ReadAsync())return;installed=r.IsDBNull(0)?null:r.GetString(0);eligible=r.GetBoolean(1);
        }
        if(eligible&&UpdatePolicy.IsUpgrade(installed,release.Version)) {
            // A terminal failure requires a deliberate DELETE retry; polling never starts an update loop.
            await UpdateService.Cmd(c,"INSERT INTO agent_updates(id,client_id,target_version,sha256,message) SELECT gen_random_uuid(),$1,$2,$3,$4 WHERE NOT EXISTS(SELECT 1 FROM agent_updates WHERE client_id=$1 AND (state IN ('Pending','Waiting','Downloading','Applying','Verifying') OR (target_version=$2 AND state IN ('Failed','RolledBack'))))",cid,release.Version,release.Sha256,UpdatePolicy.Message("Pending")).ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
    public async Task<IResult> Lifecycle(Guid cid,Database db)
    {
        await EnsureUpgrade(cid);
        var rows=await db.Query("SELECT deactivation_id AS id,NOT EXISTS(SELECT 1 FROM agent_updates WHERE client_id=$1 AND state IN ('Pending','Waiting','Downloading','Applying','Verifying')) AS ready FROM clients WHERE id=$1 AND deletion_pending",cid);
        return rows.Count==0?Results.NoContent():Results.Ok(rows[0]);
    }
    public async Task<IResult> Confirm(Guid cid,Guid instance,Guid id,Database db)
    {
        await using var c=await source.OpenConnectionAsync();await using var tx=await c.BeginTransactionAsync();
        var match=await UpdateService.Cmd(c,"SELECT id FROM clients WHERE id=$1 AND agent_instance_id=$2 AND deactivation_id=$3 AND deletion_pending AND NOT enabled FOR UPDATE",cid,instance,id).ExecuteScalarAsync();
        if(match is null) {
            var receipt=await UpdateService.Cmd(c,"SELECT id FROM deactivation_receipts WHERE client_id=$1 AND instance_id=$2 AND id=$3",cid,instance,id).ExecuteScalarAsync();
            return receipt is null?Results.NotFound():Results.NoContent();
        }
        if(await UpdateService.Cmd(c,"SELECT id FROM agent_updates WHERE client_id=$1 AND state IN ('Pending','Waiting','Downloading','Applying','Verifying') LIMIT 1",cid).ExecuteScalarAsync() is not null)return Results.Conflict(new{error="Aguarde a atualização terminar antes de confirmar."});
        await UpdateService.Cmd(c,"UPDATE clients SET deactivation_confirmed_at=COALESCE(deactivation_confirmed_at,now()) WHERE id=$1",cid).ExecuteNonQueryAsync();
        await UpdateService.Cmd(c,"INSERT INTO deactivation_receipts(id,client_id,instance_id,token_hash) SELECT deactivation_id,id,agent_instance_id,token_hash FROM clients WHERE id=$1 ON CONFLICT DO NOTHING",cid).ExecuteNonQueryAsync();
        await tx.CommitAsync();return Results.NoContent();
    }
}
public sealed class DeactivationWorker(IServiceProvider services,ILogger<DeactivationWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while(!stoppingToken.IsCancellationRequested) {
            try {
                var db=services.GetRequiredService<Database>();var deletion=services.GetRequiredService<ClientDeletionService>();var control=services.GetRequiredService<DeactivationService>();
                foreach(var row in await db.Query("SELECT id,deactivation_confirmed_at FROM clients WHERE deletion_pending ORDER BY deactivation_requested_at")) {
                    var id=(Guid)row["id"]!;
                    if(row["deactivation_confirmed_at"] is not null)await deletion.Finalize(id,stoppingToken);else await control.EnsureUpgrade(id);
                }
            }catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}catch(Exception){logger.LogWarning("Desativação pendente será tentada novamente.");}
            try{await Task.Delay(TimeSpan.FromSeconds(15),stoppingToken);}catch(OperationCanceledException){break;}
        }
    }
}
