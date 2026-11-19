using BackupControl.Agent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Diagnostics;
using System.ServiceProcess;
namespace BackupControl.Setup;

public sealed record RemoteUpdateJob(Guid Id,string TargetVersion,string Sha256,string Url,string State);
public sealed record AgentRelease(string Version,int UpdateProtocol);
public interface IUpdateReplacement
{
    Task Stop(CancellationToken ct);
    Task<bool> Idle(CancellationToken ct);
    void Snapshot();
    void Replace();
    void Restore();
    Task StartAndVerify(CancellationToken ct);
    void EndMaintenance();
    void Commit() {}
}
public static class UpdateReplacement
{
    public static async Task<string> Apply(IUpdateReplacement target,CancellationToken ct,Func<Task>? applying=null)
    {
        if(!await target.Idle(ct))return "Waiting";
        var stopAttempted=false;var changed=false;
        try {
            // Recovery is durable before stopping; code cannot change under the installer semaphore.
            target.Snapshot();
            stopAttempted=true;await target.Stop(ct);
            if(!await target.Idle(ct)){await target.StartAndVerify(ct);target.Commit();return "Waiting";}
            if(applying is not null)await applying();
            changed=true;target.Replace();
            await target.StartAndVerify(ct);target.Commit();return "Succeeded";
        }catch {
            if(changed){target.Restore();await target.StartAndVerify(CancellationToken.None);target.Commit();return "RolledBack";}
            if(stopAttempted){await target.StartAndVerify(CancellationToken.None);target.Commit();}
            throw;
        }finally{target.EndMaintenance();}
    }
}
public static class RemoteUpdater
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static string DirectoryPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"BackupControlAgentUpdater");
    public static string LockName(InstallLocations paths)=>@"Global\BackupControlSetup-"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(paths.ServiceName+"|"+paths.Data.ToUpperInvariant())));
    public static void ValidateJob(RemoteUpdateJob job,string origin,string installed)
    {
        var digest=job.Sha256;
        if(job.Id==Guid.Empty||!System.Text.RegularExpressions.Regex.IsMatch(digest,"^[a-fA-F0-9]{64}$")||!Uri.TryCreate(job.Url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.GetLeftPart(UriPartial.Authority)!=BootstrapConfig.ValidateOrigin(origin)||uri.AbsolutePath!="/api/agent/package/"+digest.ToLowerInvariant()||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.UserInfo.Length!=0)throw new SetupException("Pacote de atualização inválido.");
        if(!Version.TryParse(job.TargetVersion,out var target)||!Version.TryParse(installed,out var current)||target<=current)throw new SetupException("A atualização deve ser posterior à versão instalada.");
    }
    public static void ValidateRelease(string directory,string target)
    {
        var release=JsonSerializer.Deserialize<AgentRelease>(File.ReadAllBytes(Path.Combine(directory,"agent-release.json")),Json)??throw new SetupException("Versão ausente.");
        if(release.Version!=target||release.UpdateProtocol!=1||!File.Exists(Path.Combine(directory,"BackupControl.Agent.exe"))||!Version.TryParse(target,out var expected))throw new SetupException("Versão assinada incompatível.");
        // AssemblyName reads the PE metadata without loading or executing package code.
        System.Reflection.AssemblyName assembly;
        try{assembly=System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(directory,"BackupControl.Agent.dll"));}
        catch(Exception error) when(error is IOException or BadImageFormatException or UnauthorizedAccessException){throw new SetupException("Biblioteca do agente ausente ou inválida.");}
        var actual=assembly.Version;
        if(assembly.Name!="BackupControl.Agent"||actual is null||actual.Major!=expected.Major||actual.Minor!=expected.Minor||actual.Build!=Math.Max(expected.Build,0)||actual.Revision!=Math.Max(expected.Revision,0))throw new SetupException("A biblioteca do agente não corresponde à versão assinada.");
    }
    public static string JournalPath=>Path.Combine(DirectoryPath,"recovery-v1.json");
    public static async Task Recover(InstallLocations paths,CancellationToken ct)
    {
        if(!File.Exists(JournalPath))return;
        var workName=JsonSerializer.Deserialize<string>(File.ReadAllText(JournalPath))??throw new SetupException("Recuperação inválida.");
        if(!System.Text.RegularExpressions.Regex.IsMatch(workName,"^work-[a-f0-9]{32}$"))throw new SetupException("Caminho de recuperação inválido.");
        var work=Path.Combine(DirectoryPath,workName);PackageSafety.ValidateExistingTree(work);
        var replacement=new WindowsUpdateReplacement(paths,"",Path.Combine(work,"previous"));
        await RecoverReplacement(replacement,ct);
    }
    public static async Task RecoverReplacement(IUpdateReplacement replacement,CancellationToken ct)
    {
        // A surviving new service can have resumed backups after the previous owner died.
        // Re-enter the same safe maintenance handshake before touching its files.
        try{if(!await replacement.Idle(ct))throw new SetupException("Recuperação aguarda operações pendentes.");await replacement.Stop(ct);if(!await replacement.Idle(ct)){await replacement.StartAndVerify(ct);throw new SetupException("Recuperação aguarda operações pendentes.");}replacement.Restore();await replacement.StartAndVerify(ct);replacement.Commit();}
        finally{replacement.EndMaintenance();}
    }

    public static async Task ReconcileCompleted(IUpdateReplacement current,string state,Func<string,Task> status,CancellationToken ct)
    {
        try{await current.StartAndVerify(ct);}finally{current.EndMaintenance();}
        try{if(state=="Applying")await status("Verifying");await status("Succeeded");}catch{/* Installed target remains healthy if status delivery fails. */}
    }

    public static async Task<int> Run(CancellationToken ct)
    {
        if(!WindowsIdentity.GetCurrent().IsSystem)return 5;
        var paths=InstallLocations.Default();RemoteUpdateJob? job=null;HttpClient? http=null;string? work=null;string? centralOrigin=null;
        try {
            var agentSid=DurableAcl.AgentSid(paths.ServiceName);DurableAcl.ValidateCodeTree(DirectoryPath,agentSid);DurableAcl.ValidateCodeTree(paths.Install,agentSid);DurableAcl.ValidateControlData(paths.Data,agentSid);
            using var gate=new Semaphore(1,1,LockName(paths));if(!gate.WaitOne(0))return 0;
            try {
                WindowsInstallation.ValidateExistingService(paths.ServiceName,paths.Install,paths.Data);
                var recovered=File.Exists(JournalPath);await Recover(paths,ct);
                var store=new ProtectedStore(paths.Data);var settings=await store.Load<LocalSettings>("settings",ct)??throw new SetupException("Configuração ausente.");
                var origin=BootstrapConfig.ValidateOrigin(settings.CentralUrl);centralOrigin=origin;if(!origin.StartsWith("https://",StringComparison.Ordinal)||settings.InstanceId==Guid.Empty)throw new SetupException("Central inválida.");
                http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(5)};http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",settings.AgentToken);http.DefaultRequestHeaders.Add("X-Agent-Instance",settings.InstanceId.ToString());
                using var response=await http.GetAsync(origin+"/api/agent/updates/current",ct);if(response.StatusCode==System.Net.HttpStatusCode.NoContent)return 0;response.EnsureSuccessStatusCode();job=await response.Content.ReadFromJsonAsync<RemoteUpdateJob>(Json,ct)??throw new SetupException("Atualização ausente.");
                if(recovered){await Status("RolledBack");return 0;}
                if(job.State is "Applying" or "Verifying"){
                    // Commit can finish before the final status reaches the central server.
                    // Reconcile a healthy installed target rather than replacing it again.
                    PackageSignature.Verify(paths.Install);ValidateRelease(paths.Install,job.TargetVersion);
                    await ReconcileCompleted(new WindowsUpdateReplacement(paths,"",""),job.State,Status,ct);
                    return 0;
                }
                if(job.State is not ("Pending" or "Waiting" or "Downloading"))return 0;
                var installed=JsonSerializer.Deserialize<AgentRelease>(File.ReadAllBytes(Path.Combine(paths.Install,"agent-release.json")),Json)??throw new SetupException("Versão instalada ausente.");ValidateJob(job,origin,installed.Version);
                var state=await store.Read(ct);if(state.Current is not null||state.Outbox.Count!=0){await Status("Waiting");return 0;}
                await Status("Downloading");work=Path.Combine(DirectoryPath,"work-"+Guid.NewGuid().ToString("N"));WindowsInstallation.Restrict(work);var archive=Path.Combine(work,"package.zip");
                using(var download=await http.GetAsync(job.Url,HttpCompletionOption.ResponseHeadersRead,ct)){
                    download.EnsureSuccessStatusCode();await using var input=await download.Content.ReadAsStreamAsync(ct);await using var output=File.Create(archive);var buffer=new byte[81920];long total=0;int read;while((read=await input.ReadAsync(buffer,ct))!=0){total+=read;if(total>256L*1024*1024)throw new SetupException("Pacote muito grande.");await output.WriteAsync(buffer.AsMemory(0,read),ct);}
                }
                await using(var input=File.OpenRead(archive))if(!Convert.ToHexString(await SHA256.HashDataAsync(input,ct)).Equals(job.Sha256,StringComparison.OrdinalIgnoreCase))throw new SetupException("Hash inválido.");
                var stage=Path.Combine(work,"stage");Directory.CreateDirectory(stage);using(var input=File.OpenRead(archive))PackageSafety.Extract(input,stage);PackageSignature.Verify(stage);ValidateRelease(stage,job.TargetVersion);
                var result=await UpdateReplacement.Apply(new WindowsUpdateReplacement(paths,stage,Path.Combine(work,"previous")),ct,()=>Status("Applying"));try{if(result=="Succeeded")await Status("Verifying");await Status(result);}catch{/* Healthy service remains installed if central is temporarily offline. */}return 0;
                async Task Status(string state){using var status=await http.PostAsJsonAsync(origin+"/api/agent/updates/"+job.Id+"/status",new{state,message="Atualização do agente: "+state},ct);status.EnsureSuccessStatusCode();}
            }finally{gate.Release();}
        }catch{if(job is not null&&http is not null&&centralOrigin is not null)try{using var result=await http.PostAsJsonAsync(centralOrigin+"/api/agent/updates/"+job.Id+"/status",new{state="Failed",message="A atualização segura não pôde ser concluída."},CancellationToken.None);}catch{}return 1;}
        finally{http?.Dispose();if(work is not null&&!File.Exists(JournalPath)&&System.IO.Directory.Exists(work))try{PackageSafety.ValidateExistingTree(work);System.IO.Directory.Delete(work,true);}catch{}}
    }
}

internal sealed class WindowsUpdateReplacement(InstallLocations paths,string stage,string previous):IUpdateReplacement
{
    private readonly ProtectedStore store=new(paths.Data);private FileStream? lease;
    public async Task<bool> Idle(CancellationToken ct){var state=await store.Read(ct);return state.Current is null&&state.Outbox.Count==0;}
    public Task Stop(CancellationToken ct)=>WindowsInstallation.StopForRepair(paths.ServiceName,paths.Install,paths.Data,new Progress<string>(),ct);
    public void Snapshot(){PackageSafety.ValidateExistingTree(paths.Install);Copy(paths.Install,previous);var temporary=RemoteUpdater.JournalPath+".tmp";File.WriteAllText(temporary,JsonSerializer.Serialize(Path.GetFileName(Path.GetDirectoryName(previous))));using(var journal=File.Open(temporary,FileMode.Open,FileAccess.ReadWrite,FileShare.None))journal.Flush(true);File.Move(temporary,RemoteUpdater.JournalPath,true);}
    public void Commit()=>File.Delete(RemoteUpdater.JournalPath);
    public void Replace(){lease=new FileStream(Path.Combine(paths.Data,"agent.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);Clear(paths.Install);Copy(stage,paths.Install);}
    public void Restore(){if(!Directory.Exists(previous)||!File.Exists(Path.Combine(previous,"BackupControl.Agent.exe")))throw new SetupException("Cópia de recuperação incompleta.");PackageSafety.ValidateExistingTree(previous);using var svc=new ServiceController(paths.ServiceName);svc.Refresh();if(svc.Status!=ServiceControllerStatus.Stopped){svc.Stop();svc.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(30));}lease??=new FileStream(Path.Combine(paths.Data,"agent.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);Clear(paths.Install);Copy(previous,paths.Install);}
    public async Task StartAndVerify(CancellationToken ct){await WindowsInstallation.BeginMaintenance(paths.Data,ct);lease?.Dispose();lease=null;await WindowsInstallation.Start(paths.ServiceName,ct);await WindowsInstallation.WaitReady(paths.ServiceName,paths.Data,ct);}
    public void EndMaintenance(){lease?.Dispose();lease=null;WindowsInstallation.EndMaintenance(paths.Data);}
    private static void Clear(string path){PackageSafety.ValidateExistingTree(path);foreach(var file in Directory.GetFiles(path,"*",SearchOption.AllDirectories))File.Delete(file);}
    private static void Copy(string source,string target){PackageSafety.ValidateExistingTree(source);Directory.CreateDirectory(target);foreach(var file in Directory.GetFiles(source,"*",SearchOption.AllDirectories)){var destination=Path.Combine(target,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination,true);}}
}











