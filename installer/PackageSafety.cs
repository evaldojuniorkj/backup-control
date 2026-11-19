using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Principal;
namespace BackupControl.Setup;
public static class PackageSafety
{
    public static void ValidateExistingTree(string path)
    {
        ValidateDirectory(path);if(!Directory.Exists(path))return;
        var broad=new[]{"S-1-1-0","S-1-5-11","S-1-5-32-545","S-1-5-4","S-1-5-7"};
        var trusted=new[]{"S-1-5-18","S-1-5-32-544",WindowsIdentity.GetCurrent().User!.Value};
        bool Trusted(string sid)=>trusted.Contains(sid)||sid.StartsWith("S-1-5-80-",StringComparison.Ordinal);
        var writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
        void Check(FileSystemInfo entry){
            if(entry.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new SetupException("A instalação não aceita links ou junções nos arquivos existentes.");
            FileSystemSecurity acl=entry is DirectoryInfo d?d.GetAccessControl():((FileInfo)entry).GetAccessControl();
            if(!Trusted(acl.GetOwner(typeof(SecurityIdentifier)).Value))throw new SetupException("A pasta existente possui proprietário incompatível. Solicite revisão da equipe.");
            foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))if(rule.AccessControlType==AccessControlType.Allow&&(broad.Contains(rule.IdentityReference.Value)||!Trusted(rule.IdentityReference.Value))&&(rule.FileSystemRights&writes)!=0)throw new SetupException("A pasta existente permite gravação por outros usuários. Solicite revisão de permissões à equipe.");
        }
        Check(new DirectoryInfo(path));foreach(var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*",new EnumerationOptions{RecurseSubdirectories=true,AttributesToSkip=0,IgnoreInaccessible=false}))Check(entry);
    }
    public static string ValidateDirectory(string value)
    {
        if(!Path.IsPathFullyQualified(value))throw new SetupException("A pasta de instalação deve ser absoluta.");
        var full=Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        var prohibited=new[]{Path.GetPathRoot(full)!.TrimEnd(Path.DirectorySeparatorChar),Environment.GetFolderPath(Environment.SpecialFolder.Windows),Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)};
        if(prohibited.Any(p=>string.Equals(full,p.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)))throw new SetupException("Não use uma pasta de sistema ou raiz como diretório do agente.");
        for(DirectoryInfo? parent=new(full);parent is not null;parent=parent.Parent)if(parent.Exists&&parent.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new SetupException("A pasta do agente não pode atravessar links de diretório.");
        return full;
    }
    public static void Extract(Stream source,string directory)
    {
        var root=ValidateDirectory(directory)+Path.DirectorySeparatorChar;
        using var zip=new ZipArchive(source,ZipArchiveMode.Read,true);
        long total=0;
        foreach(var entry in zip.Entries){
            if(entry.FullName.Contains(':')||entry.FullName.StartsWith('/')||entry.FullName.StartsWith('\\')||entry.FullName.Any(char.IsControl)||entry.FullName.Contains('\\'))throw new SetupException("O pacote contém caminho inválido.");
            var target=Path.GetFullPath(Path.Combine(root,entry.FullName));
            if(!target.StartsWith(root,StringComparison.OrdinalIgnoreCase)||Path.GetExtension(target).Equals(".dat",StringComparison.OrdinalIgnoreCase)||((entry.ExternalAttributes>>16)&0xF000)==0xA000)throw new SetupException("O pacote contém entrada fora da pasta de instalação ou arquivo protegido.");
            total+=entry.Length;if(total>1024L*1024*1024)throw new SetupException("Pacote excede o tamanho permitido.");
        }
        foreach(var entry in zip.Entries){var target=Path.GetFullPath(Path.Combine(root,entry.FullName));if(entry.FullName.EndsWith('/'))Directory.CreateDirectory(target);else{Directory.CreateDirectory(Path.GetDirectoryName(target)!);entry.ExtractToFile(target,true);}}
    }
}
