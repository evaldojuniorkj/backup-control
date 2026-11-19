public record Login(string Username, string Password);
public record ClientInput(string Name, string Slug, string Database, int ExpectedIntervalHours=24, int GraceMinutes=60, bool Enabled=true, int DifferentialIntervalHours=0);
public record CommandInput(string BackupType="Full");
public record ClientStatusInput(bool Enabled);
public record AgentTelemetry(DateTimeOffset? NextFullAt,DateTimeOffset? NextDifferentialAt,DateTimeOffset? LastSuccessfulFullAt,bool HasFullBase,bool ScheduleEnabled,int AppliedFullIntervalHours,int AppliedDifferentialIntervalHours,string AppliedDatabase,DateTimeOffset? ConfigAppliedAt,string? CurrentBackupType,string? CurrentPhase,DateTimeOffset? CurrentStartedAt,string? CurrentOrigin=null,DateTimeOffset? AgentTime=null,int? PendingResultCount=null,string? LastResultError=null,string? AppliedSqlRevision=null,string? SqlConfigStatus=null,string? SqlConfigError=null);
public record Heartbeat(string MachineName,string? AgentVersion=null,AgentTelemetry? Telemetry=null,bool? RemoteUpdateSupported=null,bool? RemoteDeactivationSupported=null);
public record RunInput(Guid EventId, Guid? CommandId, string BackupType, string Status, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, long SizeBytes, string FileName, string Message);
public record StorageInput(string Host, int Port, string Username, string? Password, string BaseDirectory="/backup");
public record Storage(string Host, int Port, string Username, string EncryptedPassword, string BaseDirectory);

