using System.Security.AccessControl;
using System.Security.Principal;
using BackupControl.Setup;
using Xunit;
namespace BackupControl.Setup.Tests;
public class DurableAclTests
{
    private static readonly SecurityIdentifier Agent=new("S-1-5-80-111-222-333-444-555");
    private static readonly SecurityIdentifier OtherService=new("S-1-5-80-666-777-888-999-111");
    private static readonly SecurityIdentifier Installer=new("S-1-5-21-111-222-333-1001");
    [Theory][InlineData(true)][InlineData(false)]public void CodeBoundaryContainsNoNamedInstallerAndIsValidForSystem(bool directory)
    {
        var acl=DurableAcl.Build(directory,Agent,false);DurableAcl.ValidateDescriptor(acl,Agent,false);
        Assert.Equal("S-1-5-32-544",acl.GetOwner(typeof(SecurityIdentifier))!.Value);
        Assert.DoesNotContain(acl.GetAccessRules(true,true,typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(),r=>r.IdentityReference.Value==Installer.Value);
        Assert.Throws<SetupException>(()=>DurableAcl.ValidateDescriptor(WithWrite(acl,Installer),Agent,false));
    }
    private static FileSystemSecurity WithWrite(FileSystemSecurity acl,SecurityIdentifier sid){acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.Modify,AccessControlType.Allow));return acl;}
    [Fact]public void NamedInstallerOnlyAllowedDuringExplicitMigration()
    {
        var acl=WithWrite(DurableAcl.Build(true,Agent,true),Installer);acl.SetOwner(Installer);
        DurableAcl.ValidateDescriptor(acl,Agent,true,Installer);Assert.Throws<SetupException>(()=>DurableAcl.ValidateDescriptor(acl,Agent,true));
    }
    [Fact]public void OnlyExactAgentMayWriteControlData()
    {
        var acl=DurableAcl.Build(false,Agent,true);acl.SetOwner(Agent);DurableAcl.ValidateDescriptor(acl,Agent,true);
        Assert.Throws<SetupException>(()=>DurableAcl.ValidateDescriptor(WithWrite(acl,OtherService),Agent,true));
    }
    [Theory][InlineData("S-1-5-20")][InlineData("S-1-5-21-111-222-333-1002")]
    public void SqlIdentityNeverTrustedForCodeOrControlFiles(string sid)
    {
        Assert.Throws<SetupException>(()=>DurableAcl.ValidateDescriptor(WithWrite(DurableAcl.Build(false,Agent,true),new(sid)),Agent,true));
    }
    [Fact]public void AgentCannotWriteExecutableTree()=>Assert.Throws<SetupException>(()=>DurableAcl.ValidateDescriptor(WithWrite(DurableAcl.Build(false,Agent,false),Agent),Agent,false));
    [Fact]public void BackupSubtreesAreNotControlPaths(){Assert.DoesNotContain(DurableAcl.ControlFiles,n=>n.Contains("backup",StringComparison.OrdinalIgnoreCase)||n.Contains('/')||n.Contains('\\'));}
}
