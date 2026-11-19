namespace BackupControl.Agent;

public record LocalSettings(string CentralUrl, string AgentToken, string SqlConnectionString, string BackupDirectory, int PollSeconds = 20, int SqlTimeoutSeconds = 3600, bool AllowInsecureHttp = false, bool EnableSchedule = true, bool CopyOnlyFull = false, Guid InstanceId = default,string? SqlRevision=null);
public record StorageConfig(string Host, int Port, string Username, string Password, string Protocol, string PassiveMode);
public record SqlRuntimeConfig(string Server,string Username,string Password,bool TrustServerCertificate,string Revision);
public record ClientConfig(Guid ClientId, string Slug, string Database, int ExpectedIntervalHours, string RemoteDirectory, StorageConfig? Storage, int DifferentialIntervalHours=0,SqlRuntimeConfig? Sql=null);
public record AgentDeactivation(Guid Id,bool Ready);
public record RemoteCommand(Guid Id, string BackupType, DateTimeOffset CreatedAt);
public record BackupResult(Guid EventId, Guid? CommandId, string BackupType, string Status, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, long SizeBytes, string FileName, string Message);
public record DatabaseChangeStamp(Guid DatabaseGuid,Guid RecoveryForkGuid,string LogEndLsn,string? FullBaseLsn);
public record AgentTelemetry(DateTimeOffset? NextFullAt,DateTimeOffset? NextDifferentialAt,DateTimeOffset? LastSuccessfulFullAt,bool HasFullBase,bool ScheduleEnabled,int AppliedFullIntervalHours,int AppliedDifferentialIntervalHours,string AppliedDatabase,DateTimeOffset? ConfigAppliedAt,string? CurrentBackupType,string? CurrentPhase,DateTimeOffset? CurrentStartedAt,string? CurrentOrigin=null,DateTimeOffset? AgentTime=null,int? PendingResultCount=null,string? LastResultError=null,string? AppliedSqlRevision=null,string? SqlConfigStatus=null,string? SqlConfigError=null);
public record Operation(Guid EventId, Guid? CommandId, string BackupType, DateTimeOffset StartedAt, string Database, string Directory, string BakPath, string ZipPath, string FileName, string Phase = "Created", long SizeBytes = 0, string Sha256 = "", string? BaseLsn=null,string? DestinationId=null,string? Origin=null,DatabaseChangeStamp? ChangeStamp=null,string? ChangeDetection=null);
public record ScheduledUploadRetry(Operation Operation, DateTimeOffset RetryAt);
public class AgentState
{
    public Guid? DeactivationId {get;set;}
    public DateTimeOffset? DeactivatedAt {get;set;}
    public bool DeactivationConfirmed {get;set;}
    public DatabaseChangeStamp? SuccessfulChangeStamp {get;set;}
    public string? LastDataFileName {get;set;}
    public long LastDataFileSize {get;set;}
    public string? FullBaseFileName {get;set;}
    public long FullBaseFileSize {get;set;}
    public string? AppliedSqlRevision {get;set;}
    public string? SqlConfigStatus {get;set;}
    public string? SqlConfigError {get;set;}
    public string? LastResultError {get;set;}
    public ClientConfig? Config { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? ConfigAppliedAt {get;set;}
    public DateTimeOffset? NextDifferentialAt {get;set;}
    public DateTimeOffset? LastSuccessfulFullAt {get;set;}
    public DateTimeOffset? LastFullAttemptAt {get;set;}
    public int ConsecutiveScheduledFullFailures { get; set; }
    public DateTimeOffset? ScheduledFullRetryAt { get; set; }
    public ScheduledUploadRetry? PendingScheduledUpload { get; set; }
    public string? FullBaseLsn {get;set;}
    public string? FullBaseDatabase {get;set;}
    public string? FullBaseDestination {get;set;}
    public bool Suspended { get; set; }
    public Operation? Current { get; set; }
    public List<BackupResult> Outbox { get; set; } = [];
}
public interface IStateStore
{
    Task ClearBackupCredentials(bool clearToken,CancellationToken ct)=>throw new NotSupportedException("Persistência sem suporte à remoção de credenciais.");
    Task<AgentState> Read(CancellationToken ct = default);
    Task Write(AgentState state, CancellationToken ct = default);
}
public interface ISqlBackup
{
    Task<DatabaseChangeStamp?> ReadChangeStamp(string database,CancellationToken ct)=>Task.FromResult<DatabaseChangeStamp?>(null);
    Task ApplyConfiguration(SqlRuntimeConfig config,string database,CancellationToken ct)=>throw new NotSupportedException("Esta versão não aplica configurações SQL remotas.");
    Task Check(string database, CancellationToken ct);
    Task Backup(Operation operation, CancellationToken ct);
    Task Verify(Operation operation, CancellationToken ct);
    Task<string?> ReadBaseLsn(Operation operation,CancellationToken ct);
}
public interface IArchive
{
    Task<(long Size, string Sha256)> Create(Operation operation, CancellationToken ct);
}
public interface IUploader
{
    Task<bool?> Exists(string directory,string fileName,long size,StorageConfig storage,CancellationToken ct)=>Task.FromResult<bool?>(null);
    Task Upload(Operation operation, StorageConfig storage, CancellationToken ct);
    Task Check(string directory, StorageConfig storage, CancellationToken ct);
}
public interface ICentral
{
    Task<AgentDeactivation?> Lifecycle(CancellationToken ct)=>Task.FromResult<AgentDeactivation?>(null);
    Task ConfirmDeactivation(Guid id,CancellationToken ct)=>throw new NotSupportedException();
    Task<ClientConfig> Config(CancellationToken ct);
    Task Heartbeat(CancellationToken ct);
    Task Status(AgentTelemetry status,CancellationToken ct) => Heartbeat(ct);
    Task<RemoteCommand?> Claim(CancellationToken ct);
    Task Report(BackupResult result, CancellationToken ct);
}
public sealed class AgentUnauthorizedException : Exception;
public sealed class CentralRejectedException(int status) : Exception("Central rejeitou o resultado (HTTP " + status + ").");

