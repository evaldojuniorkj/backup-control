using System.Security.AccessControl;
using System.Security.Principal;
namespace BackupControl.Setup;

// Durable boundaries must not depend on the identity that happened to launch Setup.
public static class DurableAcl
{
    private static readonly SecurityIdentifier SystemSid=new(WellKnownSidType.LocalSystemSid,null);
    private static readonly SecurityIdentifier AdminSid=new(WellKnownSidType.BuiltinAdministratorsSid,null);
    public static readonly string[] ControlFiles=["settings.dat","state.dat","installer.dat","agent.lock","maintenance.guard","maintenance.owner","maintenance.request","maintenance.ready"];
    public static SecurityIdentifier AgentSid(string service)=> (SecurityIdentifier)new NTAccount(@"NT SERVICE\"+service).Translate(typeof(SecurityIdentifier));
    public static FileSystemSecurity Build(bool directory,SecurityIdentifier? agent,bool writableAgent)
    {
        FileSystemSecurity acl=directory?new DirectorySecurity():new FileSecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(AdminSid);
        var inheritance=directory?InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit:InheritanceFlags.None;
        foreach(var sid in new[]{SystemSid,AdminSid})acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,inheritance,PropagationFlags.None,AccessControlType.Allow));
        if(agent is not null)acl.AddAccessRule(new FileSystemAccessRule(agent,writableAgent?FileSystemRights.Modify:FileSystemRights.ReadAndExecute,inheritance,PropagationFlags.None,AccessControlType.Allow));
        return acl;
    }
    public static void ValidateDescriptor(FileSystemSecurity acl,SecurityIdentifier? agent,bool writableAgent,SecurityIdentifier? migrationInstaller=null)
    {
        bool Administrator(string sid)=>sid==SystemSid.Value||sid==AdminSid.Value||sid==migrationInstaller?.Value;
        var owner=acl.GetOwner(typeof(SecurityIdentifier))?.Value;
        if(owner is null||!Administrator(owner)&&!(writableAgent&&owner==agent?.Value))throw new SetupException("Proprietário incompatível na fronteira protegida.");
        const FileSystemRights writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
        foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow&&(rule.FileSystemRights&writes)!=0&&!Administrator(rule.IdentityReference.Value)&&!(writableAgent&&rule.IdentityReference.Value==agent?.Value&&(rule.FileSystemRights&(FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership))==0))throw new SetupException("Permissão de gravação incompatível na fronteira protegida.");
    }
    private static void CheckLink(FileSystemInfo entry){if((entry.Attributes&FileAttributes.ReparsePoint)!=0)throw new SetupException("A fronteira protegida não aceita links.");}
    private static FileSystemSecurity Read(FileSystemInfo entry)=>entry is DirectoryInfo directory?directory.GetAccessControl():((FileInfo)entry).GetAccessControl();
    private static void Set(FileSystemInfo entry,FileSystemSecurity acl){CheckLink(entry);if(entry is DirectoryInfo directory)directory.SetAccessControl((DirectorySecurity)acl);else((FileInfo)entry).SetAccessControl((FileSecurity)acl);}
    public static void NormalizeCodeTree(string path,SecurityIdentifier? agent)
    {
        PackageSafety.ValidateDirectory(path);var root=new DirectoryInfo(path);CheckLink(root);Set(root,Build(true,agent,false));
        foreach(var entry in root.EnumerateFileSystemInfos("*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=0,IgnoreInaccessible=false}))Set(entry,Build(entry is DirectoryInfo,agent,false));
    }
    public static void ValidateCodeTree(string path,SecurityIdentifier? agent)
    {
        PackageSafety.ValidateDirectory(path);var root=new DirectoryInfo(path);CheckLink(root);ValidateDescriptor(Read(root),agent,false);
        foreach(var entry in root.EnumerateFileSystemInfos("*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=0,IgnoreInaccessible=false})){CheckLink(entry);ValidateDescriptor(Read(entry),agent,false);}
    }
    public static void NormalizeControlData(string path,SecurityIdentifier agent)
    {
        PackageSafety.ValidateDirectory(path);Set(new DirectoryInfo(path),Build(true,agent,true));
        foreach(var name in ControlFiles){var file=new FileInfo(Path.Combine(path,name));if(file.Exists)Set(file,Build(false,agent,true));}
    }
    public static void ValidateControlData(string path,SecurityIdentifier? agent,SecurityIdentifier? migrationInstaller=null)
    {
        PackageSafety.ValidateDirectory(path);var root=new DirectoryInfo(path);if(!root.Exists)return;CheckLink(root);ValidateDescriptor(Read(root),agent,true,migrationInstaller);
        // Backup payloads and SQL-owned directories deliberately are not traversed.
        foreach(var name in ControlFiles){var file=new FileInfo(Path.Combine(path,name));if(file.Exists){CheckLink(file);ValidateDescriptor(Read(file),agent,true,migrationInstaller);}}
    }
    public static void ValidateBackupLinks(string path)
    {
        PackageSafety.ValidateDirectory(path);var root=new DirectoryInfo(path);if(!root.Exists)return;CheckLink(root);
        foreach(var entry in root.EnumerateFileSystemInfos("*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=0,IgnoreInaccessible=false}))CheckLink(entry);
    }
}
