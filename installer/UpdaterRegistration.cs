using System.Diagnostics;
using System.Security.Principal;
namespace BackupControl.Setup;
public static class UpdaterRegistration
{
    public static async Task Ensure(InstallLocations locations,CancellationToken ct)
    {
        if(locations.TestMode)return;
        if(locations!=InstallLocations.Default())throw new SetupException("Atualizador requer caminhos padrão.");
        var root=RemoteUpdater.DirectoryPath;PackageSafety.ValidateExistingTree(root);WindowsInstallation.Restrict(root);
        var executable=Path.Combine(root,"BackupControl.Setup.exe");
        // Keep an existing stable helper intact, including when its scheduled instance is running.
        if(!File.Exists(executable)){
            var source=Environment.ProcessPath??throw new SetupException("Instalador ausente.");
            var temporary=Path.Combine(root,"helper-"+Guid.NewGuid().ToString("N")+".tmp");try{using(var input=File.OpenRead(source)){var length=TemplateLength(input);using var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None);var buffer=new byte[81920];while(length>0){var read=input.Read(buffer,0,(int)Math.Min(length,buffer.Length));if(read==0)throw new EndOfStreamException();output.Write(buffer,0,read);length-=read;}output.Flush(true);}File.Move(temporary,executable);}finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        var xml=Path.Combine(root,"task.xml");
        WriteTaskDefinition(xml,executable);
        File.Delete(Path.Combine(root,"enabled-v1.txt"));
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"schtasks.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var a in new[]{"/Create","/TN","BackupControlAgentUpdater","/XML",xml,"/F"})info.ArgumentList.Add(a);
        using var process=Process.Start(info)??throw new SetupException("Não foi possível registrar o atualizador.");var standardOutput=process.StandardOutput.ReadToEndAsync(ct);var error=process.StandardError.ReadToEndAsync(ct);await process.WaitForExitAsync(ct);var details=(await error+" "+await standardOutput).Trim();if(process.ExitCode!=0)throw new SetupException("O Windows recusou o registro do atualizador (código "+process.ExitCode+"). "+details[..Math.Min(details.Length,1200)]);
        // The agent can read the capability marker, never modify the helper or its directory.
        WindowsInstallation.Restrict(root,@"NT SERVICE\BackupControlAgent",agentRights:System.Security.AccessControl.FileSystemRights.ReadAndExecute);
        try{File.WriteAllText(Path.Combine(root,"enabled-v1.txt"),"1");DurableAcl.NormalizeCodeTree(root,DurableAcl.AgentSid(locations.ServiceName));}catch{File.Delete(Path.Combine(root,"enabled-v1.txt"));throw;}
    }
    public static void WriteTaskDefinition(string xml,string executable)
    {
        File.WriteAllText(xml,"<?xml version=\"1.0\" encoding=\"UTF-16\"?><Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Triggers><TimeTrigger><Repetition><Interval>PT1M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><StartBoundary>2026-01-01T00:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers><Principals><Principal id=\"System\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals><Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT15M</ExecutionTimeLimit></Settings><Actions Context=\"System\"><Exec><Command>"+System.Security.SecurityElement.Escape(executable)+"</Command><Arguments>--apply-pending-update</Arguments></Exec></Actions></Task>",System.Text.Encoding.Unicode);
    }
    public static long TemplateLength(Stream input)
    {
        if(input.Length<16)throw new SetupException("Instalador inválido.");
        input.Seek(-16,SeekOrigin.End);Span<byte> footer=stackalloc byte[16];input.ReadExactly(footer);
        var payload=System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(footer[..8]);
        var appended=footer[8..].SequenceEqual("BCGEN001"u8)||footer[8..].SequenceEqual("BCSETUP1"u8);
        if(appended&&(payload<2||payload>65536||payload>input.Length-16))throw new SetupException("Identificação inválida.");
        var length=appended?input.Length-16-payload:input.Length;input.Position=0;return length;
    }
}




