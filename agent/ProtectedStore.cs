using System.Security.Cryptography;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
namespace BackupControl.Agent;

public sealed class ProtectedStore(string directory) : IStateStore
{
    public string DataDirectory=>directory;
    private static readonly byte[] Entropy = "BackupControlAgent-v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public async Task Save<T>(string name, T value, CancellationToken ct = default)
    {
        if (name is not ("settings" or "state" or "test")) throw new ArgumentException("Nome de arquivo inválido.");
        Directory.CreateDirectory(directory);
        ProtectDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        var path = Path.Combine(directory, name + ".dat");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { await file.WriteAsync(encrypted, ct); file.Flush(true); }
        File.Move(temporary, path, true);
        CryptographicOperations.ZeroMemory(bytes);
    }
    public async Task<T?> Load<T>(string name, CancellationToken ct = default)
    {
        var path = Path.Combine(directory, name + ".dat");
        if (!File.Exists(path)) return default;
        var encrypted = await File.ReadAllBytesAsync(path, ct);
        var data = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine);
        try { return JsonSerializer.Deserialize<T>(data, Json) ?? throw new IOException("Estado local inválido."); }
        finally { CryptographicOperations.ZeroMemory(data); }
    }
    public async Task ClearBackupCredentials(bool clearToken,CancellationToken ct)
    {
        var settings=await Load<LocalSettings>("settings",ct)??throw new IOException("Configuração protegida ausente.");
        await Save("settings",settings with{SqlConnectionString="",SqlRevision=null,EnableSchedule=false,AgentToken=clearToken?"":settings.AgentToken},ct);
        foreach(var file in new DirectoryInfo(directory).EnumerateFiles()){
            if(file.Name=="installer.dat"||System.Text.RegularExpressions.Regex.IsMatch(file.Name,"^(settings|state)\\.dat\\.[a-f0-9]{32}\\.tmp$|^installer\\.[a-f0-9]{32}\\.tmp$")){
                if((file.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Arquivo de credencial não pode ser link.");
                file.Delete();
            }
        }
    }
    public Task<AgentState> Read(CancellationToken ct = default) => ReadState(ct);
    private async Task<AgentState> ReadState(CancellationToken ct) => await Load<AgentState>("state", ct) ?? new();
    public Task Write(AgentState state, CancellationToken ct = default) => Save("state", state, ct);
    private static void ProtectDirectory(string directory)
    {
        var folder = new DirectoryInfo(directory);
        var current = folder.GetAccessControl();
        if (current.AreAccessRulesProtected)
        {
            string[] broad = ["S-1-1-0", "S-1-5-11", "S-1-5-32-545", "S-1-5-4", "S-1-5-7"];
            foreach (FileSystemAccessRule rule in current.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && broad.Contains(rule.IdentityReference.Value)) throw new UnauthorizedAccessException("O diretório protegido tem acesso amplo.");
            return;
        }
        var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var identities = new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), WindowsIdentity.GetCurrent().User! };
        foreach (var identity in identities.Distinct()) security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        folder.SetAccessControl(security);
    }
}
