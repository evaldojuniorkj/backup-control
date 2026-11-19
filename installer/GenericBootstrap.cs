using System.Buffers.Binary;
using System.Text.Json;
namespace BackupControl.Setup;
public sealed record GenericBootstrap(string CentralUrl)
{
    public static bool IsGeneric(Stream file)
    {
        if(!file.CanSeek||file.Length<16)return false;file.Seek(-8,SeekOrigin.End);Span<byte> marker=stackalloc byte[8];file.ReadExactly(marker);return marker.SequenceEqual("BCGEN001"u8);
    }
    public static GenericBootstrap Read(Stream file)
    {
        if(!IsGeneric(file))throw new SetupException("Instalador genérico sem configuração válida da central.");
        file.Seek(-16,SeekOrigin.End);Span<byte> footer=stackalloc byte[16];file.ReadExactly(footer);var length=BinaryPrimitives.ReadInt64LittleEndian(footer[..8]);
        if(length is <2 or >65536||length>file.Length-16)throw new SetupException("Configuração da central inválida.");
        file.Seek(-16-length,SeekOrigin.End);var bytes=new byte[(int)length];file.ReadExactly(bytes);
        try{using var json=JsonDocument.Parse(bytes);if(json.RootElement.ValueKind!=JsonValueKind.Object||json.RootElement.EnumerateObject().Count()!=1||!json.RootElement.TryGetProperty("centralUrl",out var origin)||origin.ValueKind!=JsonValueKind.String)throw new SetupException("O instalador genérico deve conter somente o endereço da central.");return new(BootstrapConfig.ValidateOrigin(origin.GetString()!));}
        catch(JsonException){throw new SetupException("Configuração da central inválida.");}
    }
}
