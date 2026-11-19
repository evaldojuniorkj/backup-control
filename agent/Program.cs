using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BackupControl.Agent;

return await Entry.Run(args);

public static class Entry
{
    public static async Task<int> Run(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("O agente requer Windows."); return 1; }
        string? Option(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        var root = Path.GetFullPath(Option("--data-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BackupControlAgent"));
        if (root.TrimEnd('\\') == Path.GetPathRoot(root)?.TrimEnd('\\') || new[] { Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.DesktopDirectory }.Any(f => string.Equals(root.TrimEnd('\\'), Environment.GetFolderPath(f).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Use um diretório exclusivo de dados do agente, nunca uma pasta de sistema.");
        Directory.CreateDirectory(root);
        // FileShare.None evita duas instâncias disputando o mesmo comando e estado.
        using var processLock = new FileStream(Path.Combine(root, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var store = new ProtectedStore(root);
        try
        {
            var initialState=await store.Read();
            if(initialState.DeactivationConfirmed){await DisabledService.Run(args);return 0;}
            if(initialState.DeactivationId is not null && (args.Contains("--configure")||Option("--configure-file") is not null||args.Contains("--check")))throw new InvalidOperationException("Este agente está permanentemente desativado.");
            bool oneShot = args.Contains("--once") || args.Contains("--run-now");
            if ((oneShot || args.Contains("--check") || args.Contains("--configure") || Option("--configure-file") is not null)
                && MaintenanceGate.Check(root, acknowledge: false))
                throw new InvalidOperationException("A instalação está em manutenção. Aguarde antes de executar um comando do agente.");
            if (args.Contains("--configure") || Option("--configure-file") is not null)
            {
                LocalSettings config;
                if (Option("--configure-file") is string file)
                    config = JsonSerializer.Deserialize<LocalSettings>(await File.ReadAllTextAsync(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new ArgumentException("Configuração vazia.");
                else
                {
                    Console.Write("URL da central [http://localhost:5180]: "); var url = Console.ReadLine();
                    Console.Write("Token do cliente (oculto): "); var token = ReadSecret();
                    Console.Write("Instância SQL [localhost]: "); var server = Console.ReadLine();
                    Console.Write("Autenticação Windows? [S/n]: "); var integrated = Console.ReadLine();
                    var cs = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder { DataSource = string.IsNullOrWhiteSpace(server) ? "localhost" : server, Encrypt = Microsoft.Data.SqlClient.SqlConnectionEncryptOption.Mandatory };
                    Console.Write("Confiar no certificado local do SQL? [s/N]: "); cs.TrustServerCertificate = string.Equals(Console.ReadLine(), "s", StringComparison.OrdinalIgnoreCase);
                    cs.IntegratedSecurity = !string.Equals(integrated, "n", StringComparison.OrdinalIgnoreCase);
                    if (!cs.IntegratedSecurity) { Console.Write("Usuário SQL: "); cs.UserID = Console.ReadLine(); Console.Write("Senha SQL (oculta): "); cs.Password = ReadSecret(); }
                    Console.Write("Pasta local de backup [" + Path.Combine(root, "backups") + "]: "); var backupDir = Console.ReadLine();
                    config = new(string.IsNullOrWhiteSpace(url) ? "http://localhost:5180" : url, token, cs.ConnectionString, string.IsNullOrWhiteSpace(backupDir) ? Path.Combine(root, "backups") : backupDir);
                }
                var previous = await store.Load<LocalSettings>("settings");
                config = config with { InstanceId = previous?.InstanceId is Guid previousId && previousId != Guid.Empty ? previousId : Guid.NewGuid() };
                Safety.Validate(config);
                var state = await store.Read();
                if (previous is not null && previous.AgentToken != config.AgentToken && state.Config is not null)
                {
                    using var check = new CentralClient(config); var fresh = await check.Config(default);
                    if (fresh.ClientId != state.Config.ClientId) throw new ArgumentException("Não vincule outro cliente ao mesmo diretório de dados.");
                }
                await store.Save("settings", config);
                Console.WriteLine("Configuração protegida com DPAPI salva. O arquivo de importação, se usado, deve ser removido após configurar.");
                return 0;
            }
            var settings = await store.Load<LocalSettings>("settings") ?? throw new ArgumentException("Agente não configurado. Execute --configure antes de iniciar.");
            if(initialState.DeactivationId is null)Safety.Validate(settings);
            else if(string.IsNullOrEmpty(settings.AgentToken)){await DisabledService.Run(args);return 0;}
            if (args.Contains("--paths")) { Console.WriteLine(JsonSerializer.Serialize(new { backupDirectory = settings.BackupDirectory, dataDirectory = root })); return 0; }
            using var instanceLease = new InstanceLease(settings.InstanceId);
            using var central = new CentralClient(settings,controlOnly:initialState.DeactivationId is not null);
            var sql = new SqlBackup(settings,store); var uploader = new FtpUploader();
            if (args.Contains("--check"))
            {
                var config = await central.Config(default); Safety.Validate(config);
                await central.Heartbeat(default);
                Console.WriteLine("Central: conexão e identificação confirmadas.");
                await sql.Check(config.Database, default);
                Console.WriteLine("SQL: banco ONLINE na máquina local. Permissão de BACKUP/VERIFYONLY será comprovada na execução.");
                if (config.Storage is null) throw new ArgumentException("Destino FTP não configurado.");
                await uploader.Check(config.RemoteDirectory, config.Storage, default);
                Console.WriteLine("FTP: conexão e escrita confirmadas em " + config.RemoteDirectory + ". Arquivo de teste removido.");
                return 0;
            }
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
            var serviceName=Option("--service-name")??"BackupControlAgent";
            if(serviceName is not ("BackupControlAgent" or "BackupControlAgentInstallerTest"))throw new ArgumentException("Nome de serviço inválido.");
            builder.Services.AddWindowsService(o => o.ServiceName = serviceName);
            builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss "; });
            if (!Environment.UserInteractive) builder.Logging.AddEventLog(o => o.SourceName = "BackupControlAgent");
            builder.Services.AddSingleton(settings); builder.Services.AddSingleton<IStateStore>(store); builder.Services.AddSingleton<ICentral>(central);
            builder.Services.AddSingleton<ISqlBackup>(sql); builder.Services.AddSingleton<IArchive, ZipArchiveService>(); builder.Services.AddSingleton<IUploader>(uploader);
            builder.Services.AddSingleton<AgentEngine>();
            if (oneShot)
            {
                using var host = builder.Build();
                using var cancel = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
                var engine = host.Services.GetRequiredService<AgentEngine>();
                await engine.Cycle(args.Contains("--run-now"), Option("--backup-type") ?? "Full", cancel.Token);
                if (engine.LastResult is not null) Console.WriteLine("RESULT " + JsonSerializer.Serialize(engine.LastResult));
                return engine.LastResult?.Status == "Failed" ? 2 : args.Contains("--run-now") && engine.LastResult is null ? 3 : 0;
            }
            builder.Services.AddHostedService<AgentWorker>();
            using (var host = builder.Build()) await host.RunAsync();
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(Safety.SafeError(e)); return 1; }
    }
    private static string ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var chars = new List<char>();
        while (true) { var key = Console.ReadKey(true); if (key.Key == ConsoleKey.Enter) break; if (key.Key == ConsoleKey.Backspace) { if (chars.Count > 0) chars.RemoveAt(chars.Count - 1); } else if (!char.IsControl(key.KeyChar)) chars.Add(key.KeyChar); }
        Console.WriteLine(); return new string(chars.ToArray());
    }
}

public sealed class AgentWorker(AgentEngine engine, LocalSettings settings, IStateStore store,ILogger<AgentWorker> log,IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Only enter maintenance between cycles, never while BACKUP or upload is running.
            if(store is ProtectedStore disk && MaintenanceGate.Check(disk.DataDirectory, acknowledge: true)) {
                await Task.Delay(300,stoppingToken);
                continue;
            }
            try { await engine.Cycle(false, "Full", stoppingToken); if(engine.PermanentlyDisabled){lifetime.StopApplication();return;} }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogError("Ciclo interrompido com estado preservado: {Error}", Safety.SafeError(e)); }
            var interval = TimeSpan.FromSeconds(settings.PollSeconds);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            while (elapsed.Elapsed < interval)
            {
                if (store is ProtectedStore storage && MaintenanceGate.Requested(storage.DataDirectory)) break;
                var delay = Math.Clamp((interval - elapsed.Elapsed).TotalMilliseconds, 1, 500);
                await Task.Delay(TimeSpan.FromMilliseconds(delay), stoppingToken);
            }
        }
    }
}

// Complete the SCM handshake even on reboot; exiting before AddWindowsService would
// look like a service failure and could trigger recovery restarts.
public static class DisabledService
{
    public static async Task Run(string[] args)
    {
        if(Environment.UserInteractive||args.Contains("--once")||args.Contains("--run-now"))return;
        var index=Array.IndexOf(args,"--service-name");var name=index>=0&&index+1<args.Length?args[index+1]:"BackupControlAgent";
        if(name is not ("BackupControlAgent" or "BackupControlAgentInstallerTest"))throw new ArgumentException("Nome de serviço inválido.");
        var builder=Host.CreateApplicationBuilder(new HostApplicationBuilderSettings{Args=[],ContentRootPath=AppContext.BaseDirectory});
        builder.Services.AddWindowsService(o=>o.ServiceName=name);builder.Services.AddHostedService<DisabledWorker>();
        using var host=builder.Build();await host.RunAsync();
    }
    private sealed class DisabledWorker(IHostApplicationLifetime lifetime):BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken ct){await Task.Yield();lifetime.StopApplication();}
    }
}
