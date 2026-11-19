using Microsoft.Extensions.Logging;
namespace BackupControl.Agent;

public sealed class AgentEngine(LocalSettings settings, IStateStore store, ICentral central, ISqlBackup sql, IArchive archive, IUploader uploader, ILogger<AgentEngine> log,TimeProvider? timeProvider=null)
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan[] FullRetryDelays =
        [RetryInterval, TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4)];
    private DateTimeOffset Now=>(timeProvider??TimeProvider.System).GetUtcNow();
    public bool PermanentlyDisabled {get;private set;}
    public BackupResult? LastResult { get; private set; }
    private volatile AgentTelemetry? latestTelemetry;
    public async Task Cycle(bool force, string type, CancellationToken ct)
    {
        using var reporting=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop=TelemetryLoop(reporting.Token);
        try {await CycleCore(force,type,ct);}
        finally {
            reporting.Cancel();
            try {await loop;} catch(OperationCanceledException) when(reporting.IsCancellationRequested) {}
            if(!ct.IsCancellationRequested && latestTelemetry is not null) await SendTelemetry(latestTelemetry,ct);
        }
    }
    private async Task TelemetryLoop(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested){await Task.Delay(TimeSpan.FromSeconds(20),ct);if(latestTelemetry is not null)await SendTelemetry(latestTelemetry,ct);}
    }
    private async Task SendTelemetry(AgentTelemetry telemetry,CancellationToken ct)
    {
        try {await central.Status(telemetry with{AgentTime=Now},ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception e){log.LogWarning("Telemetria não enviada: {Error}",Safety.SafeError(e));}
    }
    private async Task Publish(AgentState state,CancellationToken ct)
    {
        if(state.Config is null||state.Suspended){latestTelemetry=null;return;}
        var c=state.Config;
        var hasBase = HasValidFullBase(state, c);
        var nextFull = settings.EnableSchedule && state.Current?.BackupType != "Full"
            ? NextScheduledFull(state, hasBase)
            : state.NextRunAt;
        var nextDifferential = state.NextDifferentialAt;
        if (settings.EnableSchedule && state.Current is null && state.PendingScheduledUpload is {} pending)
        {
            if (pending.Operation.BackupType == "Full") nextFull = pending.RetryAt;
            else nextDifferential = pending.RetryAt;
        }
        latestTelemetry=new(nextFull,nextDifferential,state.LastSuccessfulFullAt,hasBase,
            settings.EnableSchedule,c.ExpectedIntervalHours,c.DifferentialIntervalHours,c.Database,state.ConfigAppliedAt,
            state.Current?.BackupType,state.Current?.Phase,state.Current?.StartedAt,state.Current is {} op?op.Origin??(op.CommandId.HasValue?"Manual":"Unknown"):null,
            Now,state.Outbox.Count,state.LastResultError,state.AppliedSqlRevision,state.SqlConfigStatus,state.SqlConfigError);
        await SendTelemetry(latestTelemetry,ct);
    }

    private static bool HasValidFullBase(AgentState state, ClientConfig config) =>
        state.FullBaseLsn is not null && state.FullBaseDatabase == config.Database &&
        state.FullBaseDestination == Safety.DestinationIdentity(config) && state.LastSuccessfulFullAt.HasValue;

    private DateTimeOffset? NextScheduledFull(AgentState state, bool hasBase)
    {
        var next = state.NextRunAt;
        if (state.Config!.DifferentialIntervalHours > 0 && !hasBase)
        {
            var baselineRetry = state.LastFullAttemptAt?.Add(RetryInterval) ?? Now;
            if (next is null || baselineRetry < next) next = baselineRetry;
        }
        if (state.ScheduledFullRetryAt is {} retryAt && (next is null || next < retryAt)) next = retryAt;
        return next;
    }
    private async Task<bool> DeactivateIfRequested(AgentState state,CancellationToken ct)
    {
        if(state.DeactivationConfirmed){PermanentlyDisabled=true;return true;}
        if(state.DeactivationId is null){
            AgentDeactivation? request;
            try {request=await central.Lifecycle(ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception e) when(e is HttpRequestException or TaskCanceledException or AgentUnauthorizedException or CentralRejectedException){return false;}
            if(request is null)return false;
            if(request.Id==Guid.Empty)throw new IOException("Solicitação de desativação inválida.");
            if(!request.Ready){state.Suspended=true;await store.Write(state,ct);try{await central.Heartbeat(ct);}catch(Exception e) when(e is HttpRequestException or TaskCanceledException or AgentUnauthorizedException or CentralRejectedException){}return true;}
            state.DeactivationId=request.Id;state.DeactivatedAt=Now;state.Suspended=true;
            await store.Write(state,ct); // Tombstone before removal: a crash can never resume backups.
        }
        // Cycle boundaries are serialized. No SQL command/upload can be active here.
        // Pending/recovered metadata is intentionally retired with this customer's history.
        state.Config=null;state.Current=null;state.Outbox.Clear();state.NextRunAt=null;state.NextDifferentialAt=null;
        state.PendingScheduledUpload=null;state.ScheduledFullRetryAt=null;state.ConsecutiveScheduledFullFailures=0;
        state.FullBaseLsn=null;state.FullBaseDatabase=null;state.FullBaseDestination=null;state.SuccessfulChangeStamp=null;
        state.SqlConfigError=null;state.LastResultError=null;state.SqlConfigStatus=null;state.AppliedSqlRevision=null;
        await store.Write(state,ct);
        await store.ClearBackupCredentials(false,ct); // Never ACK until both SQL/FTP caches are sanitized.
        try {
            await central.ConfirmDeactivation(state.DeactivationId.Value,ct);
            await store.ClearBackupCredentials(true,ct);
            state.DeactivationConfirmed=true;await store.Write(state,ct);PermanentlyDisabled=true;
        }catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception e) when(e is HttpRequestException or TaskCanceledException or AgentUnauthorizedException or CentralRejectedException){log.LogWarning("Desativação permanente local aplicada; confirmação será reenviada.");}
        return true;
    }
    private async Task CycleCore(bool force, string type, CancellationToken ct)
    {
        latestTelemetry=null;
        var state = await store.Read(ct);
        if(await DeactivateIfRequested(state,ct))return;
        var online = false;
        try
        {
            var fresh = await central.Config(ct); Safety.Validate(fresh);
            if (state.Config is not null && state.Config.ClientId != fresh.ClientId) throw new ArgumentException("Este diretório de dados pertence a outro cliente. Use um diretório novo para vincular outra máquina.");
            if (state.Config is not null && state.NextRunAt.HasValue && state.Config.ExpectedIntervalHours != fresh.ExpectedIntervalHours)
                state.NextRunAt = state.NextRunAt.Value.AddHours(fresh.ExpectedIntervalHours - state.Config.ExpectedIntervalHours);
            if(state.Config?.Database!=fresh.Database||(state.Config is not null&&Safety.DestinationIdentity(state.Config)!=Safety.DestinationIdentity(fresh))){
                state.FullBaseLsn=null;state.FullBaseDatabase=null;state.FullBaseDestination=null;state.LastSuccessfulFullAt=null;state.NextDifferentialAt=null;state.LastFullAttemptAt=null;
                state.PendingScheduledUpload=null;state.ScheduledFullRetryAt=null;state.ConsecutiveScheduledFullFailures=0;
            }
            if(fresh.DifferentialIntervalHours==0)state.NextDifferentialAt=null;
            else if(state.Config?.DifferentialIntervalHours!=fresh.DifferentialIntervalHours&&state.LastSuccessfulFullAt.HasValue)
                state.NextDifferentialAt=state.NextDifferentialAt.HasValue&&state.Config!.DifferentialIntervalHours>0
                    ?state.NextDifferentialAt.Value.AddHours(fresh.DifferentialIntervalHours-state.Config.DifferentialIntervalHours)
                    :state.LastSuccessfulFullAt.Value.AddHours(fresh.DifferentialIntervalHours);
            state.Config = fresh; state.Suspended = false; online = true;
            state.ConfigAppliedAt=Now;
            state.NextRunAt??=Now.AddHours(fresh.ExpectedIntervalHours);
            await central.Heartbeat(ct);
            await store.Write(state, ct);
            await Publish(state,ct);
        }
        catch (AgentUnauthorizedException) { state.Suspended = true; await store.Write(state, ct); log.LogWarning("Central recusou a credencial. Execuções suspensas."); return; }
        catch (HttpRequestException) { log.LogWarning("Central indisponível. Usando configuração local validada, se existir."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { log.LogWarning("Tempo de conexão à central esgotado."); }
        if (state.Suspended || state.Config is null) return;
        if (online){await Flush(state, ct);await Publish(state,ct);}
        if (state.Suspended) return;
        var config = state.Config;
        if(settings.CopyOnlyFull&&config.DifferentialIntervalHours>0)throw new ArgumentException("Desative CopyOnlyFull antes de agendar diferenciais.");
        if (config.Storage is null) { log.LogWarning("Destino FTP não configurado na central."); return; }
        if (state.Current is not null)
        {
            if (state.Current.Phase == "SqlStarted")
            {
                await Finish(state, "Failed", "Execução interrompida durante BACKUP. Não foi repetida; arquivo local preservado para inspeção.", ct);
            }
            else await Resume(state, config.Storage, ct);
            if (online) await Flush(state, ct);
            await Publish(state,ct);
            return;
        }
        if(config.Sql is {} desired && state.AppliedSqlRevision!=desired.Revision){
            try {
                await sql.ApplyConfiguration(desired,config.Database,ct);
                state.AppliedSqlRevision=desired.Revision;state.SqlConfigStatus="Applied";state.SqlConfigError=null;
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(Exception e){state.SqlConfigStatus="Failed";state.SqlConfigError=Safety.SafeError(e);log.LogWarning("Configuração SQL remota não aplicada: {Error}",state.SqlConfigError);}
            await store.Write(state,ct);await Publish(state,ct);
            if(state.SqlConfigStatus=="Failed")return;
        }
        else if(config.Sql is not null&&state.SqlConfigStatus!="Applied"){
            state.SqlConfigStatus="Applied";state.SqlConfigError=null;await store.Write(state,ct);await Publish(state,ct);
        }
        if (state.NextRunAt is null) { state.NextRunAt = Now.AddHours(config.ExpectedIntervalHours); await store.Write(state, ct); }
        RemoteCommand? command = null;
        if (online && state.Outbox.Count == 0)
        {
            try { command = await central.Claim(ct); }
            catch (AgentUnauthorizedException) { state.Suspended = true; await store.Write(state, ct); return; }
            catch (HttpRequestException) { log.LogWarning("Não foi possível consultar comandos. Nenhum comando presumido."); }
        }
        if (command is null && !force && state.PendingScheduledUpload is {} pending)
        {
            if (settings.EnableSchedule && pending.RetryAt <= Now && state.Outbox.Count < 100)
            {
                state.Current = pending.Operation with { StartedAt = Now };
                await store.Write(state, ct);
                await Publish(state, ct);
                await Resume(state, config.Storage, ct);
                if (online) await Flush(state, ct);
                await Publish(state, ct);
            }
            return;
        }
        var validBase = HasValidFullBase(state, config);
        var fullDue = NextScheduledFull(state, validBase) <= Now;
        var diffDue=config.DifferentialIntervalHours>0&&validBase&&state.NextDifferentialAt<=Now;
        var due = settings.EnableSchedule && (fullDue||diffDue);
        if (command is null && !force && !due) return;
        if (state.Outbox.Count >= 100) { log.LogError("Fila de resultados cheia. Corrija a comunicação com a central antes de executar novos backups."); return; }
        var backupType = command?.BackupType ?? (force ? type : fullDue?"Full":"Differential");
        var now = Now; var id = Guid.NewGuid();
        var stamp=await ChangeStamp(config.Database,ct);
        if(command is null&&!force&&validBase&&ChangeMarkerPolicy.Unchanged(state.SuccessfulChangeStamp,stamp,state.FullBaseLsn))
        {
            var protectedFiles=await ProtectedFilesPresent(state,config,ct);
            if(protectedFiles==true){
                if(backupType=="Full")state.NextRunAt=now.AddHours(config.ExpectedIntervalHours);
                if(config.DifferentialIntervalHours>0&&(backupType=="Differential"||state.NextDifferentialAt<=now))state.NextDifferentialAt=now.AddHours(config.DifferentialIntervalHours);
                state.Outbox.Add(new(id,null,backupType,"Skipped",now,Now,0,"","Sem alterações detectadas desde o último backup enviado. Arquivos de proteção confirmados no FTP; nenhuma nova cópia foi gerada."));
                await store.Write(state,ct);if(online)await Flush(state,ct);await Publish(state,ct);return;
            }
        }
        var name = Safety.FileName(config.Database, now, id, backupType);
        Directory.CreateDirectory(settings.BackupDirectory);
        state.Current = new Operation(id, command?.Id, backupType, now, config.Database, config.RemoteDirectory, Path.Combine(settings.BackupDirectory, Path.ChangeExtension(name, ".bak")), Path.Combine(settings.BackupDirectory, name), name,DestinationId:Safety.DestinationIdentity(config),Origin:command is not null||force?"Manual":"Scheduled",ChangeStamp:stamp,ChangeDetection:stamp is null?"Unavailable":"Checked");
        if (backupType == "Full") {state.NextRunAt = now.AddHours(config.ExpectedIntervalHours);state.LastFullAttemptAt=now;}
        if(backupType=="Differential")state.NextDifferentialAt=config.DifferentialIntervalHours>0?now.AddHours(config.DifferentialIntervalHours):null;
        await store.Write(state, ct);
        await Resume(state, config.Storage, ct);
        if (online) await Flush(state, ct);
        await Publish(state,ct);
    }
    private async Task Resume(AgentState state, StorageConfig storage, CancellationToken ct)
    {
        try
        {
            var op = state.Current!;
            if(op.DestinationId is not null&&op.DestinationId!=Safety.DestinationIdentity(state.Config!))throw new ArgumentException("O destino mudou durante a execução. O arquivo foi preservado; solicite outro completo.");
            if (op.Phase == "Created")
            {
                if(op.BackupType=="Differential"&&(state.FullBaseLsn is null||state.FullBaseDatabase!=op.Database||state.FullBaseDestination!=Safety.DestinationIdentity(state.Config!)))throw new ArgumentException("Execute um backup completo por este agente antes do diferencial.");
                if(op.BackupType=="Full"){state.FullBaseLsn=null;state.FullBaseDatabase=null;state.FullBaseDestination=null;state.LastSuccessfulFullAt=null;state.NextDifferentialAt=null;}
                await sql.Check(op.Database, ct);
                if (op.Origin == "Manual" && op.BackupType == "Full") SupersedePendingUpload(state);
                state.Current = op = op with { Phase = "SqlStarted" }; await store.Write(state, ct);
                await Publish(state,ct);
                log.LogInformation("Gerando backup {Type}; execução {Id}.", op.BackupType, op.EventId);
                await sql.Backup(op, ct);
                state.Current = op = op with { Phase = "BackupReady" }; await store.Write(state, ct);
                await Publish(state,ct);
            }
            if (op.Phase == "BackupReady")
            {
                await sql.Verify(op, ct);
                var baseLsn=await sql.ReadBaseLsn(op,ct);
                if(op.BackupType=="Full"&&!settings.CopyOnlyFull&&baseLsn is null)throw new IOException("Base do completo não identificada.");
                if(op.BackupType=="Differential"&&(baseLsn is null||baseLsn!=state.FullBaseLsn))
                {
                    state.FullBaseLsn=null;state.FullBaseDatabase=null;state.FullBaseDestination=null;state.LastSuccessfulFullAt=null;state.NextDifferentialAt=null;
                    throw new ArgumentException("A base diferencial mudou. Gere outro completo pelo agente; o diferencial incompatível não foi enviado.");
                }
                var zipped = await archive.Create(op, ct);
                state.Current = op = op with { Phase = "ZipReady", SizeBytes = zipped.Size, Sha256 = zipped.Sha256,BaseLsn=baseLsn }; await store.Write(state, ct);
                await Publish(state,ct);
            }
            if (op.Phase == "ZipReady")
            {
                if(op.BackupType=="Differential"&&(op.BaseLsn is null||op.BaseLsn!=state.FullBaseLsn||op.Database!=state.FullBaseDatabase))throw new ArgumentException("O diferencial não tem uma base completa confirmada nesta instalação.");
                try { await uploader.Upload(op, storage, ct); }
                catch (Exception e) when (op.Origin == "Scheduled" && op.CommandId is null && !ct.IsCancellationRequested && e is not BackupUploadRejectedException &&
                    e is IOException or TimeoutException or System.Net.Sockets.SocketException or FluentFTP.Exceptions.FtpException)
                {
                    await DeferScheduledUpload(state, op, e, ct);
                    return;
                }
                state.Current = op = op with { Phase = "Uploaded" }; await store.Write(state, ct);
            }
            if (op.Phase != "Uploaded") throw new IOException("Etapa de execução desconhecida.");
            await Finish(state, "Succeeded", "BACKUP CHECKSUM e RESTORE VERIFYONLY concluídos; ZIP enviado e tamanho remoto confirmado. SHA256 local: " + op.Sha256, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (state.Current is not null)
        {
            log.LogError("Falha na etapa {Phase}: {Error}", state.Current?.Phase, Safety.SafeError(e));
            await Finish(state, "Failed", $"Etapa {state.Current?.Phase}: {Safety.SafeError(e)}", ct);
        }
    }
    private void SupersedePendingUpload(AgentState state)
    {
        if (state.PendingScheduledUpload is null) return;
        log.LogInformation("Execução manual substituiu a retentativa de envio agendada. Arquivos anteriores preservados.");
        state.PendingScheduledUpload = null;
    }

    private async Task DeferScheduledUpload(AgentState state, Operation op, Exception error, CancellationToken ct)
    {
        var retryAt = Now.Add(RetryInterval);
        var reason = Safety.SafeError(error);
        log.LogWarning("Falha no envio: {Error}. O ZIP existente será reenviado após {RetryAt}.", reason, retryAt);
        if (state.PendingScheduledUpload is {} pending)
        {
            state.PendingScheduledUpload = pending with { RetryAt = retryAt };
            state.Current = null;
            await store.Write(state, ct);
            return;
        }

        // The central keeps each result immutable. Keep the archive name and give its retry a distinct event ID.
        state.PendingScheduledUpload = new(op with { EventId = Guid.NewGuid() }, retryAt);
        await Finish(state, "Failed", $"Etapa ZipReady: {reason} O ZIP existente será reenviado automaticamente.", ct);
    }

    private async Task Finish(AgentState state, string status, string message, CancellationToken ct)
    {
        var op = state.Current!;
        DateTimeOffset? originalBackupAt = null;
        if (state.PendingScheduledUpload is {} pending && pending.Operation.EventId == op.EventId)
        {
            originalBackupAt = pending.Operation.StartedAt;
            state.PendingScheduledUpload = null;
            message += $" Backup original iniciado em {originalBackupAt:O}.";
        }
        if (op.BackupType == "Full")
        {
            if (status == "Succeeded")
            {
                state.ConsecutiveScheduledFullFailures = 0;
                state.ScheduledFullRetryAt = null;
            }
            else if (op.Origin == "Scheduled" && state.PendingScheduledUpload?.Operation.FileName != op.FileName)
            {
                var level = Math.Clamp(state.ConsecutiveScheduledFullFailures, 0, FullRetryDelays.Length - 1);
                state.ScheduledFullRetryAt = Now.Add(FullRetryDelays[level]);
                state.ConsecutiveScheduledFullFailures = level + 1;
            }
        }
        if(status=="Succeeded"&&op.BackupType is "Full" or "Differential"){
            if (op.Origin == "Manual" && op.BackupType == "Differential") SupersedePendingUpload(state);
            state.SuccessfulChangeStamp=op.ChangeStamp;
            state.LastDataFileName=op.FileName;state.LastDataFileSize=op.SizeBytes;
            if(op.BackupType=="Full"){state.FullBaseFileName=op.FileName;state.FullBaseFileSize=op.SizeBytes;}
            if(op.ChangeDetection=="Unavailable")message+=" Detector de alterações indisponível: backup executado normalmente por segurança.";
        }
        if(status=="Succeeded"&&op.BackupType=="Full"&&!settings.CopyOnlyFull)
        {
            state.FullBaseLsn=op.BaseLsn;state.FullBaseDatabase=op.Database;state.FullBaseDestination=op.DestinationId;state.LastSuccessfulFullAt=originalBackupAt??Now;
            state.NextDifferentialAt=state.Config!.DifferentialIntervalHours>0&&op.BaseLsn is not null?state.LastSuccessfulFullAt.Value.AddHours(state.Config.DifferentialIntervalHours):null;
        }
        var result = new BackupResult(op.EventId, op.CommandId, op.BackupType, status, op.StartedAt, Now, op.SizeBytes, op.FileName, message);
        state.Outbox.Add(result); state.Current = null; LastResult = result;
        await store.Write(state, ct);
        log.LogInformation("Execução {Id} finalizada: {Status}. Resultado salvo localmente.", result.EventId, status);
    }
    private async Task Flush(AgentState state, CancellationToken ct)
    {
        foreach (var result in state.Outbox.ToArray())
        {
            try
            {
                await central.Report(result, ct);
                state.Outbox.Remove(result);state.LastResultError=null; await store.Write(state, ct);
                if (result.Status == "Succeeded") Cleanup(result.FileName);
            }
            catch (AgentUnauthorizedException) { state.Suspended = true; await store.Write(state, ct); return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { state.LastResultError=Safety.SafeError(e);await store.Write(state,ct);log.LogWarning("Resultado {Id} preservado para reenvio: {Error}", result.EventId, state.LastResultError); return; }
        }
    }
    private async Task<DatabaseChangeStamp?> ChangeStamp(string database,CancellationToken ct)
    {
        try{return await sql.ReadChangeStamp(database,ct);}
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception){log.LogInformation("Detector de alterações indisponível. A rotina continuará normalmente.");return null;}
    }
    private async Task<bool?> ProtectedFilesPresent(AgentState state,ClientConfig config,CancellationToken ct)
    {
        if(state.FullBaseFileName is null||state.FullBaseFileSize<=0||state.LastDataFileName is null||state.LastDataFileSize<=0)return false;
        try{
            if(await uploader.Exists(config.RemoteDirectory,state.FullBaseFileName,state.FullBaseFileSize,config.Storage!,ct)!=true)return false;
            return state.LastDataFileName==state.FullBaseFileName||await uploader.Exists(config.RemoteDirectory,state.LastDataFileName,state.LastDataFileSize,config.Storage!,ct)==true;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception){return null;}
    }
    private void Cleanup(string filename)
    {
        if (Path.GetFileName(filename) != filename || !filename.EndsWith(".zip", StringComparison.Ordinal)) return;
        foreach (var name in new[] { filename, Path.ChangeExtension(filename, ".bak") })
        {
            var path = Path.GetFullPath(Path.Combine(settings.BackupDirectory, name));
            if (!path.StartsWith(Path.GetFullPath(settings.BackupDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { log.LogWarning("Arquivo local preservado após confirmação: sem permissão para limpeza."); }
        }
    }
}
