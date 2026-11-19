using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
namespace BackupControl.Setup;

public sealed class SetupException(string message):Exception(message);
public sealed record BootstrapConfig(string CentralUrl,string EnrollmentKey,Guid ClientId,string ClientName)
{
    public static string ValidateOrigin(string value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0||uri.AbsolutePath!="/")throw new SetupException("O instalador não contém uma origem HTTPS válida. Gere outro pelo painel.");
        return uri.GetLeftPart(UriPartial.Authority);
    }
    public static BootstrapConfig Read(Stream file)
    {
        if(!file.CanSeek||file.Length<16)throw new SetupException("Instalador sem identificação. Baixe o instalador específico do cliente pelo painel.");
        file.Seek(-16,SeekOrigin.End);Span<byte> tail=stackalloc byte[16];file.ReadExactly(tail);
        var length=BinaryPrimitives.ReadInt64LittleEndian(tail[..8]);
        if(!tail[8..].SequenceEqual("BCSETUP1"u8)||length is <2 or >65536||length>file.Length-16)throw new SetupException("Identificação do instalador ausente ou inválida. Gere outro pelo painel.");
        file.Seek(-16-length,SeekOrigin.End);var payload=new byte[(int)length];file.ReadExactly(payload);
        BootstrapConfig? config;
        try {config=JsonSerializer.Deserialize<BootstrapConfig>(payload,new JsonSerializerOptions(JsonSerializerDefaults.Web));}catch(JsonException){throw new SetupException("O instalador contém uma identificação inválida.");}
        if(config is null||config.ClientId==Guid.Empty||string.IsNullOrWhiteSpace(config.EnrollmentKey)||config.EnrollmentKey.Length is <32 or >200||config.EnrollmentKey.Any(char.IsControl)||string.IsNullOrWhiteSpace(config.ClientName)||config.ClientName.Length>120||config.ClientName.Any(char.IsControl))throw new SetupException("Dados do instalador incompletos. Gere outro pelo painel.");
        return config with{CentralUrl=ValidateOrigin(config.CentralUrl)};
    }
}
public sealed record SqlInstallConfig(string Server,string Username,string Password,bool TrustServerCertificate,string ServiceIdentity,string BackupDirectory);
public sealed record PackageDownload(string Url,string Sha256);
public sealed record EnrollmentReply(Guid ClientId,string ClientName,string CentralUrl,string AgentToken,SqlInstallConfig Sql,PackageDownload Package);
