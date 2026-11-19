using BackupControl.Agent;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace BackupControl.Setup;

public static class WindowsInstallation
{
    public static void Restrict(string path,string? agentIdentity=null,string? sqlIdentity=null,FileSystemRights agentRights=FileSystemRights.Modify)
    {
        PackageSafety.ValidateDirectory(path);Directory.CreateDirectory(path);
        var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
        IdentityReference[] identities=[new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null),WindowsIdentity.GetCurrent().User!];
        var inheritance=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit;
        foreach(var identity in identities.Distinct())security.AddAccessRule(new FileSystemAccessRule(identity,FileSystemRights.FullControl,inheritance,PropagationFlags.None,AccessControlType.Allow));
        if(!string.IsNullOrWhiteSpace(agentIdentity))security.AddAccessRule(new FileSystemAccessRule(Account(agentIdentity),agentRights,inheritance,PropagationFlags.None,AccessControlType.Allow));
        if(!string.IsNullOrWhiteSpace(sqlIdentity))security.AddAccessRule(new FileSystemAccessRule(Account(sqlIdentity),FileSystemRights.Modify,inheritance,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    private static IdentityReference Account(string value)=>value.StartsWith("S-1-",StringComparison.Ordinal)?new SecurityIdentifier(value):new NTAccount(value);
    public static string SqlIdentity(string server,string configured)
    {
        if(!string.IsNullOrWhiteSpace(configured))return configured;
        var name=server.Split(',')[0];var slash=name.IndexOf('\\');var service=slash<0?"MSSQLSERVER":"MSSQL$"+name[(slash+1)..];
        using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+service);
        var identity=key?.GetValue("ObjectName") as string;
        return identity switch{"LocalSystem"=>"S-1-5-18","NT AUTHORITY\\NetworkService"=>"S-1-5-20","NT AUTHORITY\\LocalService"=>"S-1-5-19",null or ""=>throw new SetupException("Não foi possível identificar a conta do serviço SQL. Configure a identidade do serviço SQL no painel e tente novamente."),_=>identity};
    }
    public static bool Exists(string name)=>ServiceController.GetServices().Any(s=>s.ServiceName.Equals(name,StringComparison.OrdinalIgnoreCase));
    public static void ValidateExistingService(string name,string install,string data)
    {
        using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+name);
        var binary=key?.GetValue("ImagePath") as string;var account=key?.GetValue("ObjectName") as string;
        var expected='"'+Path.Combine(install,"BackupControl.Agent.exe")+"\" --data-dir \""+data+'"';
        if(name=="BackupControlAgentInstallerTest")expected+=" --service-name "+name;
        if(!string.Equals(binary,expected,StringComparison.OrdinalIgnoreCase)||!string.Equals(account,@"NT SERVICE\"+name,StringComparison.OrdinalIgnoreCase))throw new SetupException("O serviço existente usa caminhos ou conta personalizados. É necessário reparo assistido; nada foi alterado.");
    }
    public static async Task Command(string executable,IEnumerable<string> arguments,CancellationToken ct)
    {
        if(executable!="sc.exe")throw new SetupException("Utilitário de serviço não permitido.");
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,executable)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var argument in arguments)info.ArgumentList.Add(argument);
        using var process=Process.Start(info)??throw new SetupException("Não foi possível iniciar a configuração do serviço.");
        var output=process.StandardOutput.ReadToEndAsync(ct);var error=process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);await output;await error;
        if(process.ExitCode!=0)throw new SetupException("O Windows recusou a configuração do serviço. Verifique permissões administrativas e a identidade do serviço.");
    }
    public static async Task Start(string name,CancellationToken ct)
    {
        using var service=new ServiceController(name);service.Refresh();if(service.Status==ServiceControllerStatus.Running)return;
        service.Start();await Task.Run(()=>service.WaitForStatus(ServiceControllerStatus.Running,TimeSpan.FromSeconds(30)),ct);
    }
    public static async Task StopForRepair(string name,string install,string data,IProgress<string> progress,CancellationToken ct,bool allowLegacyReinstallation=false)
    {
        using var service=new ServiceController(name);service.Refresh();if(service.Status==ServiceControllerStatus.Stopped)return;
        if(!File.Exists(Path.Combine(install,"maintenance-v1.txt"))){
            if(!allowLegacyReinstallation)throw new SetupException("A versão instalada requer a opção Reinstalar agente. Nenhum backup foi interrompido.");
            var state=await new ProtectedStore(data).Read(ct);
            if(state.Current is not null)throw new SetupException("O agente antigo possui operação pendente. Aguarde antes de reinstalar.");
            progress.Report("Parando o serviço antigo para reinstalação; seu estado será conferido novamente…");
            service.Stop();await Task.Run(()=>service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(30)),ct);
            return;
        }
        progress.Report("Preparando reparo seguro; aguardando o agente ficar ocioso…");
        await BeginMaintenance(data,ct);
        try {
            await WaitReady(name,data,ct);
            service.Stop();await Task.Run(()=>service.WaitForStatus(ServiceControllerStatus.Stopped,TimeSpan.FromSeconds(30)),ct);
        }catch{EndMaintenance(data);throw;}
    }
    public static void EndMaintenance(string data)=>MaintenanceGate.End(data);

    public static Task BeginMaintenance(string data,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        MaintenanceGate.Begin(data);
        return Task.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint Type,State,Controls,ExitCode,SpecificExitCode,CheckPoint,WaitHint,ProcessId,Flags;
    }

    [DllImport("advapi32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(SafeHandle handle,int infoLevel,out ServiceStatusProcess status,int size,out int required);

    public static async Task WaitReady(string name,string data,CancellationToken ct)
    {
        using var service=new ServiceController(name);
        using var handle=service.ServiceHandle;
        var elapsed=Stopwatch.StartNew();
        while(elapsed.Elapsed<TimeSpan.FromSeconds(70)){
            ct.ThrowIfCancellationRequested();
            if(!QueryServiceStatusEx(handle,0,out var status,Marshal.SizeOf<ServiceStatusProcess>(),out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if(status.State==(uint)ServiceControllerStatus.Stopped)
                throw new SetupException("O serviço parou antes de confirmar a manutenção.");
            if(status.State==(uint)ServiceControllerStatus.Running && status.ProcessId>0){
                try {
                    using var process=Process.GetProcessById((int)status.ProcessId);
                    if(MaintenanceGate.IsReady(data,process.Id,process.StartTime.ToUniversalTime().Ticks))return;
                }
                catch(ArgumentException) { /* Service process exited between SCM and process queries. */ }
                catch(InvalidOperationException) { /* Process has terminated. */ }
            }
            await Task.Delay(300,ct);
        }
        throw new SetupException("A versão do agente não confirmou a manutenção ou ainda executa um backup. Aguarde a conclusão e confira a versão antes de tentar novamente.");
    }
}
