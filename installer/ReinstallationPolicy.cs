namespace BackupControl.Setup;
using BackupControl.Agent;
public sealed record ReinstallationApproval(string PreviousOrigin,string TargetOrigin,Guid InstanceId,Guid ClientId);
public static class ReinstallationPolicy {
 public const string ProductionOrigin="https://backup.startxsistemas.com.br";
 public static ReinstallationApproval Plan(LocalSettings previous,string requested,Guid client){
  var old=BootstrapConfig.ValidateOrigin(previous.CentralUrl);var target=BootstrapConfig.ValidateOrigin(requested);
  if(target!=ProductionOrigin||previous.InstanceId==Guid.Empty||client==Guid.Empty)throw new SetupException("Reinstalação permitida somente para a central definitiva e uma identidade existente reconhecida.");
  return new(old,target,previous.InstanceId,client);
 }
 public static void Validate(LocalSettings previous,string requested,Guid client,ReinstallationApproval? approval){
  var old=BootstrapConfig.ValidateOrigin(previous.CentralUrl);var target=BootstrapConfig.ValidateOrigin(requested);
  if(old==target&&approval is null)return;
  var expected=Plan(previous,requested,client);
  if(approval is null||approval.TargetOrigin!=expected.TargetOrigin||approval.InstanceId!=expected.InstanceId||approval.ClientId!=expected.ClientId||(old!=target&&approval.PreviousOrigin!=expected.PreviousOrigin))throw new SetupException("Confirme a reinstalação para a central definitiva neste instalador. Nenhuma credencial foi enviada.");
 }
}
