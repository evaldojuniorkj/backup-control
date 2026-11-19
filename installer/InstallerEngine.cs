using BackupControl.Agent;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.ServiceProcess;
using System.Text.Json;
namespace BackupControl.Setup;

public sealed record InstallLocations(string Install,string Data,string ServiceName,bool TestMode=false)
{
    public static InstallLocations Default()=>new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"BackupControlAgent"),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"BackupControlAgent"),"BackupControlAgent");
}
public sealed record InstallOutcome(string ClientName,string MachineName,bool DifferentialEnabled);
public sealed record EnrollmentJournal(Guid InstanceId,Guid ClientId);
public sealed class InstallerEngine(BootstrapConfig bootstrap,InstallLocations locations,IProgress<string> progress,Guid? approvedInstance=null,ReinstallationApproval? reinstallation=null)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly byte[] Entropy="BackupControlSetup-v1"u8.ToArray();
    private void Step(string value)=>progress.Report(value);
    public async Task<InstallOutcome> Install(CancellationToken ct)
    {
        var install=PackageSafety.ValidateDirectory(locations.Install);var data=PackageSafety.ValidateDirectory(locations.Data);
        var lockName=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(locations.ServiceName+"|"+data.ToUpperInvariant())));
        using var installerLock=new Semaphore(1,1,@"Global\BackupControlSetup-"+lockName);
        if(!installerLock.WaitOne(0))throw new SetupException("Outra instalação ou reparo já está em andamento. Aguarde sua conclusão.");
        try {return await InstallLocked(install,data,ct);}finally{installerLock.Release();}
    }
    private async Task<InstallOutcome> InstallLocked(string install,string data,CancellationToken ct)
    {
        if(install.Equals(data,StringComparison.OrdinalIgnoreCase)||install.StartsWith(data+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||data.StartsWith(install+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new SetupException("As pastas de instalação e dados devem ser separadas.");
        var hasService=WindowsInstallation.Exists(locations.ServiceName);
        PackageSafety.ValidateExistingTree(install);DurableAcl.ValidateControlData(data,hasService?DurableAcl.AgentSid(locations.ServiceName):null,System.Security.Principal.WindowsIdentity.GetCurrent().User);
        var store=new ProtectedStore(data);
        var previous=await store.Load<LocalSettings>("settings",ct);
        if(previous is not null)ReinstallationPolicy.Validate(previous,bootstrap.CentralUrl,bootstrap.ClientId,reinstallation);
        var state=await store.Read(ct);
        if(state.Current is not null)throw new SetupException("Há um backup em andamento ou pendente de retomada. Aguarde antes de instalar ou reparar.");
        var journal=ReadJournal(data);
        if(journal?.InstanceId==Guid.Empty||journal is not null&&previous is not null&&journal.InstanceId!=previous.InstanceId)throw new SetupException("As identidades locais da instalação não correspondem. Solicite revisão da equipe.");
        if(journal is not null&&journal.ClientId!=bootstrap.ClientId||state.Config is not null&&state.Config.ClientId!=bootstrap.ClientId)throw new SetupException("Esta máquina já possui uma instalação de outro cliente. Nada foi substituído.");
        if(hasService&&previous is null)throw new SetupException("Há um serviço existente sem configuração reconhecida. Solicite revisão da equipe.");
        if(hasService)WindowsInstallation.ValidateExistingService(locations.ServiceName,install,data);
        if(previous is not null&&previous.InstanceId==Guid.Empty)throw new SetupException("A instalação antiga não tem identidade válida. É necessária migração assistida.");
        if(previous is not null&&state.Config is null&&journal is null){
            using var old=new CentralClient(previous);var owner=await old.Config(ct);if(owner.ClientId!=bootstrap.ClientId)throw new SetupException("A instalação existente pertence a outro cadastro.");
        }
        var instance=ResolveInstance(previous?.InstanceId,journal?.InstanceId,approvedInstance);
        var wasRunning=false;var repairStopped=false;var createdService=false;var filesReplaced=false;var committed=false;FileStream? offlineLease=null;
        LocalSettings? recoverySettings=previous;
        HashSet<string>? originalFiles=null;
        var previousAcls=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        void RememberAcl(string path){if(Directory.Exists(path)&&!previousAcls.ContainsKey(path))previousAcls[path]=new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);}
        RememberAcl(data);RememberAcl(install);if(previous is not null)RememberAcl(previous.BackupDirectory);
        var cache=Path.Combine(data,"install-cache");var package=Path.Combine(cache,"agent-"+Guid.NewGuid().ToString("N")+".zip");var stage=Path.Combine(cache,"stage-"+Guid.NewGuid().ToString("N"));var oldFiles=Path.Combine(cache,"previous-"+Guid.NewGuid().ToString("N"));
        try {
            Step("Identificando a instalação e preparando acesso administrativo…");
            if(hasService){using var service=new ServiceController(locations.ServiceName);wasRunning=service.Status==ServiceControllerStatus.Running;await WindowsInstallation.StopForRepair(locations.ServiceName,install,data,progress,ct,reinstallation is not null);repairStopped=true;state=await store.Read(ct);if(state.Current is not null)throw new SetupException("O agente ainda possui uma operação pendente. Reparo adiado.");}
            WindowsInstallation.Restrict(data);WindowsInstallation.Restrict(cache);
            try{offlineLease=new FileStream(Path.Combine(data,"agent.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){throw new SetupException("Há outra execução do agente nesta máquina. Aguarde antes de instalar ou reparar.");}
            state=await store.Read(ct);if(state.Current is not null)throw new SetupException("Há uma operação pendente do agente. Reparo adiado.");
            WriteJournal(data,new(instance,bootstrap.ClientId));
            using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(5)};
            Step("Conectando à central e recebendo a configuração deste cliente…");
            using var redeem=await http.PostAsJsonAsync(bootstrap.CentralUrl+"/api/agent/enrollment/redeem",new{enrollmentKey=bootstrap.EnrollmentKey,instanceId=instance,machineName=Environment.MachineName,existingAgentToken=previous?.AgentToken},ct);
            if(!redeem.IsSuccessStatusCode)throw new SetupException((int)redeem.StatusCode switch{401 or 403=>"A autorização foi recusada, revogada ou expirou. Solicite nova aprovação da máquina pelo instalador genérico.",409=>"Vínculo ou credencial local incompatível. Se o token foi trocado no painel, solicite recuperação assistida à equipe.",429=>"Muitas tentativas de instalação. Aguarde alguns minutos.",_=>"A central não concluiu a matrícula (HTTP "+(int)redeem.StatusCode+"). Confira o endereço e a configuração no painel."});
            var reply=await redeem.Content.ReadFromJsonAsync<EnrollmentReply>(Json,ct)??throw new SetupException("A central devolveu uma configuração vazia.");
            ValidateReply(bootstrap,reply);
            var sql=reply.Sql;
            var backup=PackageSafety.ValidateDirectory(locations.TestMode?Path.Combine(data,"backups"):string.IsNullOrWhiteSpace(sql.BackupDirectory)?Path.Combine(data,"backups"):sql.BackupDirectory);
            ValidateBackupDirectory(backup,data,install);DurableAcl.ValidateBackupLinks(backup);
            var connection=new SqlConnectionStringBuilder{DataSource=sql.Server,UserID=sql.Username,Password=sql.Password,IntegratedSecurity=false,Encrypt=SqlConnectionEncryptOption.Mandatory,TrustServerCertificate=sql.TrustServerCertificate}.ConnectionString;
            var settings=previous is null?new LocalSettings(reply.CentralUrl,reply.AgentToken,connection,backup,InstanceId:instance):previous with{CentralUrl=reply.CentralUrl,AgentToken=reply.AgentToken,SqlConnectionString=connection,BackupDirectory=backup,AllowInsecureHttp=false};
            // Repair rotates the server token. Preserve the previous SQL/runtime options on failure, but save the usable new token immediately.
            var recovery=previous is null?settings:previous with{CentralUrl=reply.CentralUrl,AgentToken=reply.AgentToken};
            recoverySettings=recovery;
            await store.Save("settings",recovery,ct);
            http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",reply.AgentToken);http.DefaultRequestHeaders.Add("X-Agent-Instance",instance.ToString());
            Step("Baixando o serviço Windows e verificando o hash SHA256 do pacote…");
            using(var response=await http.GetAsync(reply.Package.Url,HttpCompletionOption.ResponseHeadersRead,ct)){
                if(!response.IsSuccessStatusCode)throw new SetupException("Não foi possível baixar o pacote autenticado do agente.");
                await using var source=await response.Content.ReadAsStreamAsync(ct);await using var target=new FileStream(package,FileMode.Create,FileAccess.Write,FileShare.None,81920,true);
                var buffer=new byte[81920];long count=0;int read;
                while((read=await source.ReadAsync(buffer,ct))>0){count+=read;if(count>256L*1024*1024)throw new SetupException("O pacote excede o tamanho esperado.");await target.WriteAsync(buffer.AsMemory(0,read),ct);}
            }
            await using(var input=File.OpenRead(package)){var actual=Convert.ToHexString(await SHA256.HashDataAsync(input,ct));if(!actual.Equals(reply.Package.Sha256,StringComparison.OrdinalIgnoreCase))throw new SetupException("O pacote baixado não corresponde ao hash informado pela central. Tente novamente ou gere outro instalador.");}
            Directory.CreateDirectory(stage);await using(var input=File.OpenRead(package))PackageSafety.Extract(input,stage);
            PackageSignature.Verify(stage);
            if(!File.Exists(Path.Combine(stage,"BackupControl.Agent.exe")))throw new SetupException("O pacote não contém o agente Windows.");
            Step("Testando o cadastro e a comunicação com a API…");
            using var central=new CentralClient(settings);var config=await central.Config(ct);Safety.Validate(config);
            if(config.ClientId!=bootstrap.ClientId)throw new SetupException("A configuração recebida pertence a outro cliente.");
            // Enrollment retries retain their token/package snapshot; SQL credentials are current API configuration.
            sql=ResolveSql(sql,config.Sql);
            settings=settings with{SqlConnectionString=new SqlConnectionStringBuilder{DataSource=sql.Server,UserID=sql.Username,Password=sql.Password,IntegratedSecurity=false,Encrypt=SqlConnectionEncryptOption.Mandatory,TrustServerCertificate=sql.TrustServerCertificate}.ConnectionString,SqlRevision=config.Sql?.Revision};
            Step("Testando SQL Server e identificando a conta do serviço SQL…");
            await new SqlBackup(settings).Check(config.Database,ct);
            var sqlIdentity=WindowsInstallation.SqlIdentity(sql.Server,sql.ServiceIdentity);
            RememberAcl(backup);
            WindowsInstallation.Restrict(backup,sqlIdentity:sqlIdentity);
            Step("Testando o FTP e a pasta de backups do cliente…");
            if(config.Storage is null)throw new SetupException("O destino FTP não está configurado na central.");
            await new FtpUploader().Check(config.RemoteDirectory,config.Storage,ct);
            Step("Instalando arquivos e configurando o serviço automático…");
            WindowsInstallation.Restrict(install);
            if(hasService){Directory.CreateDirectory(oldFiles);originalFiles=SafeFiles(install).Select(f=>Path.GetRelativePath(install,f)).ToHashSet(StringComparer.OrdinalIgnoreCase);CopyTree(install,oldFiles);}
            filesReplaced=true;foreach(var file in SafeFiles(install))File.Delete(file);CopyTree(stage,install);
            var exe=Path.Combine(install,"BackupControl.Agent.exe");
            if(!hasService){
                var binary='"'+exe+"\" --data-dir \""+data+'"'+(locations.TestMode?" --service-name "+locations.ServiceName:"");
                await WindowsInstallation.Command("sc.exe",["create",locations.ServiceName,"binPath=",binary,"start=","auto","DisplayName=",locations.TestMode?"Backup Control Installer Test":"Backup Control Agent"],ct);createdService=true;
                await WindowsInstallation.Command("sc.exe",["config",locations.ServiceName,"obj=",@"NT SERVICE\"+locations.ServiceName],ct);
            }
            var identity=@"NT SERVICE\"+locations.ServiceName;
            WindowsInstallation.Restrict(install,identity,agentRights:FileSystemRights.ReadAndExecute);
            WindowsInstallation.Restrict(data,identity);WindowsInstallation.Restrict(backup,identity,sqlIdentity);
            await store.Save("settings",settings,ct);
            await WindowsInstallation.Command("sc.exe",["failure",locations.ServiceName,"reset=","86400","actions=","restart/30000/restart/60000/restart/120000"],ct);
            if(!EventLog.SourceExists("BackupControlAgent"))EventLog.CreateEventSource("BackupControlAgent","Application");
            Step("Iniciando o serviço e confirmando o contato com a central…");
            await central.Heartbeat(ct);
            // Hold the worker before its first cycle until every fallible preflight step succeeds.
            await WindowsInstallation.BeginMaintenance(data,ct);
            offlineLease.Dispose();offlineLease=null;
            await WindowsInstallation.Start(locations.ServiceName,ct);
            await WindowsInstallation.WaitReady(locations.ServiceName,data,ct);
            var durableAgentSid=DurableAcl.AgentSid(locations.ServiceName);
            DurableAcl.NormalizeCodeTree(install,durableAgentSid);
            DurableAcl.NormalizeControlData(data,durableAgentSid);
            await UpdaterRegistration.Ensure(locations,ct);
            committed=true;
            WindowsInstallation.EndMaintenance(data);
            Step("Instalação concluída. Central, SQL e FTP aprovados; serviço em execução.");
            return new(reply.ClientName,Environment.MachineName,config.DifferentialIntervalHours>0);
        }
        catch {
            if(committed){Step("O serviço já foi instalado. Verifique seu estado no painel antes de qualquer reparo; nenhum backup em andamento foi interrompido.");throw;}
            try {
                if(createdService){using var svc=new ServiceController(locations.ServiceName);svc.Refresh();if(svc.Status!=ServiceControllerStatus.Stopped){svc.Stop();svc.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(20));}await WindowsInstallation.Command("sc.exe",["delete",locations.ServiceName],CancellationToken.None);}
                if(hasService&&filesReplaced&&Directory.Exists(oldFiles)){
                    using(var service=new ServiceController(locations.ServiceName)){service.Refresh();if(service.Status!=ServiceControllerStatus.Stopped){service.Stop();service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(30));}}
                    if(originalFiles is not null)foreach(var file in SafeFiles(install))if(!originalFiles.Contains(Path.GetRelativePath(install,file)))File.Delete(file);
                    CopyTree(oldFiles,install);
                }
                if(hasService&&recoverySettings is not null)await store.Save("settings",recoverySettings,CancellationToken.None);
                WindowsInstallation.EndMaintenance(data);
                foreach(var acl in previousAcls){if(!Directory.Exists(acl.Key))continue;var security=new DirectorySecurity();security.SetSecurityDescriptorSddlForm(acl.Value,AccessControlSections.Access);new DirectoryInfo(acl.Key).SetAccessControl(security);}
                offlineLease?.Dispose();offlineLease=null;
                if(hasService&&repairStopped&&wasRunning)await WindowsInstallation.Start(locations.ServiceName,CancellationToken.None);
            }catch{Step("O reparo não pôde restaurar completamente o serviço anterior. Contate a equipe antes de tentar novamente.");}
            throw;
        }
        finally{offlineLease?.Dispose();foreach(var path in new[]{stage,oldFiles})if(Directory.Exists(path)){try{PackageSafety.ValidateExistingTree(path);Directory.Delete(path,true);}catch{}}if(File.Exists(package))try{File.Delete(package);}catch{}}
    }
    public static void ValidateRepairOrigin(string previous,string requested)
    {
        if(BootstrapConfig.ValidateOrigin(previous)!=BootstrapConfig.ValidateOrigin(requested))throw new SetupException("A central deste instalador difere da instalação existente. Migração de endereço exige procedimento assistido; nenhuma credencial foi enviada.");
    }
    public static Guid ResolveInstance(Guid? previous,Guid? journal,Guid? approved)
    {
        if(approved==Guid.Empty||approved.HasValue&&(previous.HasValue&&previous!=approved||journal.HasValue&&journal!=approved))throw new SetupException("A aprovação pertence a outra identidade de instalação. Nada foi substituído.");
        return previous??journal??approved??Guid.NewGuid();
    }
    public static void ValidateBackupDirectory(string backup,string data,string install)
    {
        static bool Contains(string parent,string child)=>child.Equals(parent,StringComparison.OrdinalIgnoreCase)||child.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        if(Contains(install,backup)||Contains(backup,install)||Contains(backup,data)||(Contains(data,backup)&&!Contains(Path.Combine(data,"backups"),backup)))throw new SetupException("Use uma pasta exclusiva de backups, fora do programa e do cache do instalador.");
    }
    public static void ValidateReply(BootstrapConfig bootstrap,EnrollmentReply reply)
    {
        if(reply.ClientId!=bootstrap.ClientId||BootstrapConfig.ValidateOrigin(reply.CentralUrl)!=bootstrap.CentralUrl||string.IsNullOrEmpty(reply.AgentToken)||reply.AgentToken.Length<32||reply.Sql is null||reply.Package is null||string.IsNullOrWhiteSpace(reply.Sql.Server)||string.IsNullOrWhiteSpace(reply.Sql.Username)||string.IsNullOrEmpty(reply.Sql.Password))throw new SetupException("A resposta da central não corresponde ao instalador deste cliente.");
        var digest=reply.Package.Sha256??"";
        if(!System.Text.RegularExpressions.Regex.IsMatch(digest,"^[a-fA-F0-9]{64}$")||!Uri.TryCreate(reply.Package.Url,UriKind.Absolute,out var package)||package.GetLeftPart(UriPartial.Authority)!=bootstrap.CentralUrl||package.AbsolutePath!="/api/agent/package/"+digest.ToLowerInvariant()||package.Query.Length>0||package.Fragment.Length>0||package.UserInfo.Length>0)throw new SetupException("O pacote informado pela central não é válido para esta instalação.");
    }
    public static SqlInstallConfig ResolveSql(SqlInstallConfig initial,SqlRuntimeConfig? current)=>current is null?initial:initial with{Server=current.Server,Username=current.Username,Password=current.Password,TrustServerCertificate=current.TrustServerCertificate};
    public static EnrollmentJournal? ReadJournal(string data)
    {
        var path=Path.Combine(data,"installer.dat");if(!File.Exists(path))return null;
        var bytes=ProtectedData.Unprotect(File.ReadAllBytes(path),Entropy,DataProtectionScope.LocalMachine);
        try{return JsonSerializer.Deserialize<EnrollmentJournal>(bytes)??throw new SetupException("Identidade de instalação inválida.");}finally{CryptographicOperations.ZeroMemory(bytes);}
    }
    private static void WriteJournal(string data,EnrollmentJournal value)
    {
        var plain=JsonSerializer.SerializeToUtf8Bytes(value);try{var protectedBytes=ProtectedData.Protect(plain,Entropy,DataProtectionScope.LocalMachine);var temporary=Path.Combine(data,"installer."+Guid.NewGuid().ToString("N")+".tmp");File.WriteAllBytes(temporary,protectedBytes);File.Move(temporary,Path.Combine(data,"installer.dat"),true);}finally{CryptographicOperations.ZeroMemory(plain);}
    }
    private static void CopyTree(string source,string target)
    {
        foreach(var file in SafeFiles(source)){var destination=Path.Combine(target,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(file,destination,true);}
    }
    private static IEnumerable<string> SafeFiles(string source)=>Directory.EnumerateFiles(source,"*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=FileAttributes.ReparsePoint,IgnoreInaccessible=false});
}



