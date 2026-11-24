using System.Text.RegularExpressions;
namespace BackupControl.Agent;

public static class Safety
{
    public static string DestinationIdentity(ClientConfig config)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new{Host=config.Storage?.Host.ToLowerInvariant(),config.Storage?.Port,config.Storage?.Username,config.RemoteDirectory}))));
    public static string RemoteDirectory(string root, string slug)
    {
        if (!Regex.IsMatch(slug ?? "", "^[a-z0-9][a-z0-9_-]{1,62}$") || !Regex.IsMatch(root ?? "", "^(/[a-zA-Z0-9_-]+)+/?$")) throw new ArgumentException("Diretório de cliente inválido.");
        return root!.TrimEnd('/') + "/" + slug;
    }
    public static string SqlIdentifier(string database)
    {
        if (string.IsNullOrWhiteSpace(database) || database.Length > 128 || database.Any(char.IsControl)) throw new ArgumentException("Nome do banco inválido.");
        return "[" + database.Replace("]", "]]") + "]";
    }
    public static string BackupSql(string database, string type, bool copyOnly = false)
    {
        var name = SqlIdentifier(database);
        return type switch
        {
            "Full" => $"BACKUP DATABASE {name} TO DISK = @path WITH CHECKSUM, INIT{(copyOnly ? ", COPY_ONLY" : "")};",
            "Differential" when !copyOnly => $"BACKUP DATABASE {name} TO DISK = @path WITH CHECKSUM, INIT, DIFFERENTIAL;",
            "Log" when !copyOnly => $"BACKUP LOG {name} TO DISK = @path WITH CHECKSUM, INIT;",
            _ => throw new ArgumentException("Tipo de backup inválido ou incompatível com CopyOnlyFull.")
        };
    }
    public static string FileName(string database, DateTimeOffset time, Guid id, string type)
    {
        if (type is not ("Full" or "Differential" or "Log")) throw new ArgumentException("Tipo inválido.");
        var safe = Regex.Replace(database, "[^a-zA-Z0-9_-]", "_");
        if (safe.Length > 70) safe = safe[..70];
        return $"{safe}_{time.UtcDateTime:yyyyMMdd_HHmmss}_{id:N}_{type.ToLowerInvariant()}.zip";
    }
    public static void Validate(LocalSettings settings)
    {
        if (!Uri.TryCreate(settings.CentralUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/" || uri.Scheme is not ("http" or "https")) throw new ArgumentException("URL da central inválida. Use somente a origem, sem /api.");
        if (uri.Scheme != "https" && !uri.IsLoopback && !settings.AllowInsecureHttp) throw new ArgumentException("Use HTTPS fora de localhost. HTTP em rede de teste exige autorização explícita na configuração.");
        if (settings.AgentToken.Length < 32 || string.IsNullOrWhiteSpace(settings.SqlConnectionString) || !Path.IsPathFullyQualified(settings.BackupDirectory) || settings.PollSeconds is < 5 or > 3600 || settings.SqlTimeoutSeconds is < 30 or > 86400) throw new ArgumentException("Configuração do agente inválida.");
    }
    public static void Validate(ClientConfig config)
    {
        SqlIdentifier(config.Database);
        if(config.DifferentialIntervalHours<0||config.DifferentialIntervalHours>=config.ExpectedIntervalHours)throw new ArgumentException("O intervalo diferencial deve ser zero ou menor que o intervalo completo.");
        var root = config.RemoteDirectory[..config.RemoteDirectory.LastIndexOf('/')];
        if (RemoteDirectory(root, config.Slug) != config.RemoteDirectory || config.ExpectedIntervalHours is < 1 or > 8760) throw new ArgumentException("Configuração de cliente inválida.");
        if (config.Storage is not null && (config.Storage.Protocol != "FTP" || config.Storage.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(config.Storage.Host))) throw new ArgumentException("Destino FTP inválido.");
    }
    public static string SafeError(Exception error)
    {
        if (error is Microsoft.Data.SqlClient.SqlException sql) return $"SQL Server: erro {sql.Number}. Verifique permissões, estado do banco e diretório local. Consulte o evento local para a etapa.";
        return error switch
        {
            BackupUploadRejectedException => error.Message,
            FtpTransferException => error.Message,
            UnauthorizedAccessException => "Acesso negado ao diretório de backup ou ao arquivo de configuração.",
            AgentUnauthorizedException => "Token recusado ou cliente pausado na central.",
            CentralRejectedException => error.Message,
            ArgumentException => error.Message,
            OperationCanceledException => "Operação interrompida. Arquivos locais preservados.",
            _ => $"Falha na etapa de execução ({error.GetType().Name}). Verifique SQL, disco e conexão FTP."
        };
    }
}
