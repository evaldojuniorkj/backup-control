namespace BackupControl.Setup;
public static class SetupArguments
{
    public static InstallLocations Resolve(string[] args)
    {
        if(args.Length==0)return InstallLocations.Default();
        if(args.Length!=4||args[0]!="--test-root"||args[2]!="--service-name"||args[3]!="BackupControlAgentInstallerTest")throw new SetupException("Parâmetros de teste inválidos. Nenhuma instalação foi iniciada.");
        var root=PackageSafety.ValidateDirectory(args[1]);var production=InstallLocations.Default();
        foreach(var protectedRoot in new[]{production.Data,production.Install}){
            var target=Path.GetFullPath(protectedRoot);
            if(root.Equals(target,StringComparison.OrdinalIgnoreCase)||root.StartsWith(target+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||target.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new SetupException("O teste não pode usar ou conter a instalação real do agente.");
        }
        return new(Path.Combine(root,"program"),Path.Combine(root,"data"),"BackupControlAgentInstallerTest",true);
    }
}
