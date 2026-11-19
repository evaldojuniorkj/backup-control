using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Security.AccessControl;
namespace BackupControl.Setup;
public sealed record DeviceRegistration(Guid RegistrationId,Guid InstanceId,string DeviceSecret,string RequestedName,string MachineName,string CentralUrl)
{
    public static DeviceRegistration Create(string origin,string name,Guid instance)
    {
        if(instance==Guid.Empty||string.IsNullOrWhiteSpace(name)||name.Trim().Length>120||name.Any(char.IsControl))throw new SetupException("Informe o nome do cliente, com até 120 caracteres.");
        return new(Guid.NewGuid(),instance,Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),name.Trim(),Environment.MachineName,BootstrapConfig.ValidateOrigin(origin));
    }
}
public sealed record DeviceRegistrationStatus(Guid RegistrationId,string Status,string Code,DateTimeOffset ExpiresAt,string MachineName,string RequestedName,Guid? ClientId=null,string? ClientName=null,string? CentralUrl=null,string? EnrollmentKey=null);
public sealed class GenericRegistrationStore(string data)
{
    private static readonly byte[] Entropy="BackupControl.GenericRegistration.v1"u8.ToArray();
    public DeviceRegistration? Load()
    {
        PackageSafety.ValidateExistingTree(data);var path=Path.Combine(data,"registration.dat");if(!File.Exists(path))return null;
        var plain=ProtectedData.Unprotect(File.ReadAllBytes(path),Entropy,DataProtectionScope.LocalMachine);
        try{var value=JsonSerializer.Deserialize<DeviceRegistration>(plain)??throw new SetupException("Cadastro local inválido.");if(value.RegistrationId==Guid.Empty||value.InstanceId==Guid.Empty||!System.Text.RegularExpressions.Regex.IsMatch(value.DeviceSecret??"","^[a-f0-9]{64}$"))throw new SetupException("Identificação local inválida.");BootstrapConfig.ValidateOrigin(value.CentralUrl);return value;}
        finally{CryptographicOperations.ZeroMemory(plain);}
    }
    public void Save(DeviceRegistration value)
    {
        PackageSafety.ValidateDirectory(data);
        if(!Directory.Exists(data))WindowsInstallation.Restrict(data);
        PackageSafety.ValidateExistingTree(data);
        var acl=new DirectoryInfo(data).GetAccessControl();
        var broad=new[]{"S-1-1-0","S-1-5-11","S-1-5-32-545","S-1-5-4","S-1-5-7"};
        foreach(System.Security.AccessControl.FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(System.Security.Principal.SecurityIdentifier)))if(rule.AccessControlType==System.Security.AccessControl.AccessControlType.Allow&&broad.Contains(rule.IdentityReference.Value))throw new SetupException("O diretório de cadastro permite acesso amplo. Solicite revisão das permissões.");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(value);var temporary=Path.Combine(data,"registration."+Guid.NewGuid().ToString("N")+".tmp");
        try{File.WriteAllBytes(temporary,ProtectedData.Protect(bytes,Entropy,DataProtectionScope.LocalMachine));File.Move(temporary,Path.Combine(data,"registration.dat"),true);}
        finally{CryptographicOperations.ZeroMemory(bytes);if(File.Exists(temporary))File.Delete(temporary);}
    }
    public void Complete(){PackageSafety.ValidateExistingTree(data);File.Delete(Path.Combine(data,"registration.dat"));}
}
public sealed class GenericEnrollmentClient(string origin,HttpClient http)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public async Task<DeviceRegistrationStatus> Register(DeviceRegistration registration,string? previousToken,CancellationToken ct)
    {
        ValidateRegistrationOrigin(registration);
        using var response=await http.PostAsJsonAsync(origin+"/api/agent/registrations",new{registrationId=registration.RegistrationId,instanceId=registration.InstanceId,deviceSecret=registration.DeviceSecret,machineName=registration.MachineName,requestedName=registration.RequestedName,existingAgentToken=previousToken},ct);
        return await Read(registration,response,ct);
    }
    public async Task<DeviceRegistrationStatus> Poll(DeviceRegistration registration,CancellationToken ct)
    {
        ValidateRegistrationOrigin(registration);
        using var request=new HttpRequestMessage(HttpMethod.Get,origin+"/api/agent/registrations/"+registration.RegistrationId.ToString("D"));request.Headers.Add("X-Registration-Secret",registration.DeviceSecret);
        using var response=await http.SendAsync(request,ct);return await Read(registration,response,ct);
    }
    public async Task<DeviceRegistrationStatus> Resume(DeviceRegistration registration,string? previousToken,CancellationToken ct)
    {
        ValidateRegistrationOrigin(registration);
        using var request=new HttpRequestMessage(HttpMethod.Get,origin+"/api/agent/registrations/"+registration.RegistrationId.ToString("D"));request.Headers.Add("X-Registration-Secret",registration.DeviceSecret);
        using var response=await http.SendAsync(request,ct);
        // A resumed request retains its immutable initial inputs even after redemption saves a token locally.
        if(response.StatusCode==System.Net.HttpStatusCode.Unauthorized)return await Register(registration,previousToken,ct);
        return await Read(registration,response,ct);
    }
    private void ValidateRegistrationOrigin(DeviceRegistration value){if(BootstrapConfig.ValidateOrigin(origin)!=BootstrapConfig.ValidateOrigin(value.CentralUrl))throw new SetupException("O cadastro local pertence a outra central. Nenhuma credencial foi enviada.");}
    private static async Task<DeviceRegistrationStatus> Read(DeviceRegistration expected,HttpResponseMessage response,CancellationToken ct)
    {
        if(!response.IsSuccessStatusCode)throw new SetupException((int)response.StatusCode switch{401 or 403=>"A central não reconheceu a identificação deste cadastro.",409=>"O cadastro já possui outro vínculo. Verifique a máquina e solicite revisão da equipe.",429=>"Muitas solicitações de cadastro. Aguarde alguns minutos e tente novamente.",_=>"A central não concluiu o cadastro da máquina (HTTP "+(int)response.StatusCode+")."});
        var status=await response.Content.ReadFromJsonAsync<DeviceRegistrationStatus>(Json,ct)??throw new SetupException("Resposta de cadastro vazia.");
        if(status.RegistrationId!=expected.RegistrationId||status.Status is not ("Pending" or "Approved" or "Rejected" or "Expired")||status.Code is null||!System.Text.RegularExpressions.Regex.IsMatch(status.Code,"^[A-Z0-9-]{6,24}$"))throw new SetupException("Resposta de cadastro incompatível com esta máquina.");
        if(status.Status=="Approved")ApprovedBootstrap(expected,status);
        else if(status.EnrollmentKey is not null)throw new SetupException("A central enviou autorização antes da aprovação.");
        return status;
    }
    public static BootstrapConfig ApprovedBootstrap(DeviceRegistration expected,DeviceRegistrationStatus status)
    {
        if(status.Status!="Approved"||status.RegistrationId!=expected.RegistrationId||status.ClientId is null||status.ClientId==Guid.Empty||string.IsNullOrWhiteSpace(status.ClientName)||status.ClientName.Length>120||status.ClientName.Any(char.IsControl)||status.CentralUrl is null||BootstrapConfig.ValidateOrigin(status.CentralUrl)!=expected.CentralUrl||!System.Text.RegularExpressions.Regex.IsMatch(status.EnrollmentKey??"","^[a-fA-F0-9]{64}$"))throw new SetupException("A aprovação não corresponde a esta central e máquina.");
        return new(expected.CentralUrl,status.EnrollmentKey!,status.ClientId.Value,status.ClientName);
    }
}
