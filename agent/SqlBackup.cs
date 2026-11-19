using Microsoft.Data.SqlClient;
namespace BackupControl.Agent;

public sealed class SqlBackup(LocalSettings settings,ProtectedStore? store=null) : ISqlBackup
{
    private LocalSettings activeSettings=settings;
    public async Task<DatabaseChangeStamp?> ReadChangeStamp(string database,CancellationToken ct)
    {
        Safety.SqlIdentifier(database);
        await using var connection=await Open(ct);await using var command=connection.CreateCommand();
        command.CommandTimeout=15;
        command.CommandText="SELECT database_guid,recovery_fork_guid FROM sys.database_recovery_status WHERE database_id=DB_ID(@database); SELECT log_end_lsn FROM sys.dm_db_log_stats(DB_ID(@database)); SELECT differential_base_lsn FROM sys.master_files WHERE database_id=DB_ID(@database) AND type=0;";
        command.Parameters.AddWithValue("@database",database);
        await using var r=await command.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct)||r.IsDBNull(0)||r.IsDBNull(1))return null;
        var databaseId=r.GetGuid(0);var fork=r.GetGuid(1);
        if(!await r.NextResultAsync(ct)||!await r.ReadAsync(ct)||r.IsDBNull(0))return null;
        var lsn=r.GetString(0);
        if(!await r.NextResultAsync(ct))return null;
        var bases=new List<string?>();while(await r.ReadAsync(ct))bases.Add(r.IsDBNull(0)?null:Convert.ToString(r.GetValue(0),System.Globalization.CultureInfo.InvariantCulture));
        var distinct=bases.Distinct(StringComparer.Ordinal).ToArray();
        return new(databaseId,fork,lsn,distinct.Length==1?distinct[0]:null);
    }
    public async Task ApplyConfiguration(SqlRuntimeConfig config,string database,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(config.Server)||string.IsNullOrWhiteSpace(config.Username)||string.IsNullOrEmpty(config.Password)||string.IsNullOrWhiteSpace(config.Revision))throw new ArgumentException("Configuração SQL compartilhada incompleta.");
        if(activeSettings.SqlRevision==config.Revision)return;
        var cs=new SqlConnectionStringBuilder{DataSource=config.Server,UserID=config.Username,Password=config.Password,IntegratedSecurity=false,Encrypt=SqlConnectionEncryptOption.Mandatory,TrustServerCertificate=config.TrustServerCertificate};
        var candidate=activeSettings with{SqlConnectionString=cs.ConnectionString,SqlRevision=config.Revision};
        await new SqlBackup(candidate).Check(database,ct);
        if(store is null)throw new InvalidOperationException("Persistência protegida indisponível para configuração SQL remota.");
        await store.Save("settings",candidate,ct);
        activeSettings=candidate;
    }
    private async Task<SqlConnection> Open(CancellationToken ct)
    {
        var cs = new SqlConnectionStringBuilder(activeSettings.SqlConnectionString) { InitialCatalog = "master", ConnectTimeout = 15, ApplicationName = "BackupControlAgent" };
        var connection = new SqlConnection(cs.ConnectionString);
        try { await connection.OpenAsync(ct); return connection; } catch { await connection.DisposeAsync(); throw; }
    }
    public async Task Check(string database, CancellationToken ct)
    {
        Safety.SqlIdentifier(database);
        await using var connection = await Open(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(SERVERPROPERTY('MachineName') AS nvarchar(128)), DATABASEPROPERTYEX(@database,'Status');";
        command.Parameters.AddWithValue("@database", database);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || !string.Equals(reader.GetString(0), Environment.MachineName, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Instale o agente na mesma máquina do SQL Server. Esta versão não usa pasta compartilhada para SQL remoto.");
        if (reader.IsDBNull(1) || !string.Equals(reader.GetString(1), "ONLINE", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Banco não encontrado, inacessível ou fora de ONLINE.");
    }
    public async Task Backup(Operation op, CancellationToken ct)
    {
        // A execução é exclusivamente nativa; não copia MDF/LDF em uso.
        await using var connection = await Open(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = Safety.BackupSql(op.Database, op.BackupType, settings.CopyOnlyFull);
        command.CommandTimeout = settings.SqlTimeoutSeconds;
        command.Parameters.AddWithValue("@path", op.BakPath);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task Verify(Operation op, CancellationToken ct)
    {
        await using var connection = await Open(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM;";
        command.CommandTimeout = settings.SqlTimeoutSeconds;
        command.Parameters.AddWithValue("@path", op.BakPath);
        await command.ExecuteNonQueryAsync(ct);
    }
    public async Task<string?> ReadBaseLsn(Operation op,CancellationToken ct)
    {
        if(op.BackupType=="Log")return null;
        await using var connection=await Open(ct);
        await using var command=connection.CreateCommand();
        command.CommandText="RESTORE HEADERONLY FROM DISK=@path;";
        command.CommandTimeout=settings.SqlTimeoutSeconds;
        command.Parameters.AddWithValue("@path",op.BakPath);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))throw new IOException("Cabeçalho do backup ausente.");
        var value=reader[op.BackupType=="Full"?"CheckpointLSN":"DatabaseBackupLSN"];
        return value is DBNull?null:Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);
    }
}
