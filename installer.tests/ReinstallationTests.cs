using BackupControl.Setup;
using BackupControl.Agent;
using Xunit;
public class ReinstallationTests {
 const string Production="https://backup.startxsistemas.com.br";
 static LocalSettings Previous()=>new("https://old.trycloudflare.com",new string('a',64),"SQL","C:\\backups",InstanceId:Guid.NewGuid());
 [Fact] public void CrossOriginRequiresExplicitMatchingConsent(){var p=Previous();var client=Guid.NewGuid();Assert.Throws<SetupException>(()=>ReinstallationPolicy.Validate(p,Production,client,null));var consent=ReinstallationPolicy.Plan(p,Production,client);ReinstallationPolicy.Validate(p,Production,client,consent);Assert.Throws<SetupException>(()=>ReinstallationPolicy.Validate(p,Production,Guid.NewGuid(),consent));Assert.Throws<SetupException>(()=>ReinstallationPolicy.Validate(p with{InstanceId=Guid.NewGuid()},Production,client,consent));}
 [Fact] public void RetryAfterRedemptionKeepsConsentBoundToSameTargetAndIdentity(){var p=Previous();var client=Guid.NewGuid();var consent=ReinstallationPolicy.Plan(p,Production,client);ReinstallationPolicy.Validate(p with{CentralUrl=Production},Production,client,consent);Assert.Throws<SetupException>(()=>ReinstallationPolicy.Validate(p with{CentralUrl=Production},Production,Guid.NewGuid(),consent));}
 [Fact] public void ReinstallationNeverAuthorizesArbitraryDestination(){Assert.Throws<SetupException>(()=>ReinstallationPolicy.Plan(Previous(),"https://other.example.com",Guid.NewGuid()));Assert.Throws<SetupException>(()=>ReinstallationPolicy.Plan(Previous(),Production,Guid.Empty));}
 [Fact] public void SameOriginRepairRemainsCompatible(){var p=Previous() with{CentralUrl=Production};ReinstallationPolicy.Validate(p,Production,Guid.NewGuid(),null);Assert.Throws<SetupException>(()=>ReinstallationPolicy.Plan(p with{InstanceId=Guid.Empty},Production,Guid.NewGuid()));}
}
