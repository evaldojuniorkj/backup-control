using BackupControl.Setup;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;
public class BootstrapTests
{
    [Fact] public void RejectsExistingDirectoryWritableByUntrustedGroup()
    {
        var root=Path.Combine(Path.GetTempPath(),"bc-acl-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try{
            var folder=new DirectoryInfo(root);var acl=folder.GetAccessControl();acl.SetAccessRuleProtection(true,false);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));folder.SetAccessControl(acl);
            PackageSafety.ValidateExistingTree(root);
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-546"),FileSystemRights.Modify,AccessControlType.Allow));folder.SetAccessControl(acl);
            Assert.Throws<SetupException>(()=>PackageSafety.ValidateExistingTree(root));
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public void RejectsUnsignedPublisherPackage()
    {
        var root=Path.Combine(Path.GetTempPath(),"bc-signature-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try{File.WriteAllText(Path.Combine(root,"BackupControl.Agent.exe"),"untrusted");Assert.Throws<SetupException>(()=>PackageSignature.Verify(root));}finally{Directory.Delete(root,true);}
    }
    [Fact] public void RepairCannotDiscloseTokenToAnotherAuthority()
    {
        InstallerEngine.ValidateRepairOrigin("https://central.example.com/", "https://central.example.com");
        Assert.Throws<SetupException>(()=>InstallerEngine.ValidateRepairOrigin("https://old.example.com", "https://new.example.com"));
    }
    [Fact] public void BackupCannotOverlapProgramOrInstallerCache()
    {
        var root=Path.Combine(Path.GetTempPath(),"bc-path-tests");var data=Path.Combine(root,"data");var install=Path.Combine(root,"program");
        InstallerEngine.ValidateBackupDirectory(Path.Combine(data,"backups"),data,install);
        Assert.Throws<SetupException>(()=>InstallerEngine.ValidateBackupDirectory(Path.Combine(data,"install-cache","stage"),data,install));
        Assert.Throws<SetupException>(()=>InstallerEngine.ValidateBackupDirectory(Path.Combine(install,"backups"),data,install));
    }
    [Fact] public void ReadsOnlyBoundedTrailerAndClientIdentity()
    {
        var id=Guid.NewGuid();var payload=JsonSerializer.SerializeToUtf8Bytes(new{centralUrl="https://central.example.com",enrollmentKey=new string('a',64),clientId=id,clientName="Empresa"});
        using var file=new MemoryStream();file.Write(new byte[128]);file.Write(payload);file.Write(BitConverter.GetBytes((long)payload.Length));file.Write(Encoding.ASCII.GetBytes("BCSETUP1"));file.Position=0;
        var result=BootstrapConfig.Read(file);Assert.Equal(id,result.ClientId);Assert.Equal("https://central.example.com",result.CentralUrl);
    }
    [Theory][InlineData("http://public.example.com")][InlineData("https://example.com/api")][InlineData("https://user:pass@example.com")][InlineData("https://example.com?key=secret")]
    public void RejectsUnsafeOrigins(string url)=>Assert.Throws<SetupException>(()=>BootstrapConfig.ValidateOrigin(url));
    [Fact] public void RejectsOversizedOrMissingTrailer()
    {
        using var empty=new MemoryStream(new byte[40]);Assert.Throws<SetupException>(()=>BootstrapConfig.Read(empty));
        using var large=new MemoryStream();large.Write(BitConverter.GetBytes(100000L));large.Write("BCSETUP1"u8);large.Position=0;Assert.Throws<SetupException>(()=>BootstrapConfig.Read(large));
    }
    [Theory][InlineData("../evil.exe")][InlineData("C:/evil.exe")][InlineData("sub/../../evil.exe")][InlineData("settings.dat")]
    public void RejectsEscapingOrProtectedEntries(string entry)
    {
        var root=Path.Combine(Path.GetTempPath(),"bc-zip-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try{using var buffer=new MemoryStream();using(var zip=new ZipArchive(buffer,ZipArchiveMode.Create,true)){using var writer=new StreamWriter(zip.CreateEntry(entry).Open());writer.Write("x");}buffer.Position=0;Assert.Throws<SetupException>(()=>PackageSafety.Extract(buffer,root));}finally{Directory.Delete(root,true);}
    }
    [Fact] public void DoesNotAcceptSystemRootAsWorkingDirectory()=>Assert.Throws<SetupException>(()=>PackageSafety.ValidateDirectory(Path.GetPathRoot(Environment.SystemDirectory)!));
    [Fact] public void InvalidQaArgumentsNeverFallBackToProduction()
    {
        Assert.Throws<SetupException>(()=>SetupArguments.Resolve(["--test-root","x"]));
        Assert.Throws<SetupException>(()=>SetupArguments.Resolve(["--test-root",InstallLocations.Default().Data,"--service-name","BackupControlAgentInstallerTest"]));
        Assert.Throws<SetupException>(()=>SetupArguments.Resolve(["--test-root",Path.GetTempPath(),"--service-name","BackupControlAgent"]));
        var qa=SetupArguments.Resolve(["--test-root",Path.Combine(Path.GetTempPath(),"bc-qa"),"--service-name","BackupControlAgentInstallerTest"]);
        Assert.True(qa.TestMode);Assert.NotEqual(InstallLocations.Default().Data,qa.Data);
    }
    [Fact] public void RejectsOtherClientOrCrossOriginPackage()
    {
        var bootstrap=new BootstrapConfig("https://central.example.com",new string('a',64),Guid.NewGuid(),"Empresa");
        var reply=new EnrollmentReply(bootstrap.ClientId,"Empresa",bootstrap.CentralUrl,new string('b',64),new("localhost","sa","secret",true,"",""),new("https://central.example.com/api/agent/package/"+new string('c',64),new string('c',64)));
        InstallerEngine.ValidateReply(bootstrap,reply);
        Assert.Throws<SetupException>(()=>InstallerEngine.ValidateReply(bootstrap,reply with{ClientId=Guid.NewGuid()}));
        Assert.Throws<SetupException>(()=>InstallerEngine.ValidateReply(bootstrap,reply with{Package=reply.Package with{Url="https://other.example.com/api/agent/package"}}));
    }
}
