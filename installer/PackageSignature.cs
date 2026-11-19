using System.Security.Cryptography;
using System.Text.Json;
namespace BackupControl.Setup;
public static class PackageSignature
{
    private const string PublicKey="MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAv/Lu71QIWEp9H3HY8YIuYbVNZHaKQA8yk+yopbTch+TDmgGq1nxFf4063v4Bv4vHIWJF5yii4CdkwCZgdQikdjE9yveu5bNyJqRfQnu3ejjbTV5zrvKuc7Vh8HJhBCwSJ8qoMD8WmxMfKWkO5M8cnVC7tPKk7ZaW/7u98r2cCHKrgZq9aqW8LPcaRb5e0NzzmgxxxWTF/Wl/9TLMqL+MeOr8mIeaZ7l+DGLahV3lRK8h5pqbu+42dmPiXgV8Ak2PUESG95RT+aGq14qIb3ZIEjO+6wXp+B2DOlnRRNIRdm8zAhRLmq90+GOrTIsK5rj+u8ZhNSAoNzZLkUuO5A/G5TzqI4f6+or17m0pWXe/mnYF9hxHbcT9FNOxZuKS1lVR306zIkHQSIrAbRlkN7g+6TQJl7SIGtPz8cAT1Q3a/OnodbAJEGNNVmA3/1tBj5ilvU9fOmqRjlr3hiuU2BHyyIs/Q/I2lJUFvqEgjaxXygsQl5H9XY3mBmP0+tC3VJ7VAgMBAAE=";
    public static void Verify(string directory)
    {
        var manifestPath=Path.Combine(directory,"package-manifest.json");var signaturePath=Path.Combine(directory,"package-manifest.sig");
        if(!File.Exists(manifestPath)||!File.Exists(signaturePath))throw new SetupException("O pacote não possui assinatura do publicador autorizado.");
        var manifest=File.ReadAllBytes(manifestPath);using var rsa=RSA.Create();rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey),out _);
        if(!rsa.VerifyData(manifest,File.ReadAllBytes(signaturePath),HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1))throw new SetupException("A assinatura do pacote do agente não é válida.");
        var files=JsonSerializer.Deserialize<Dictionary<string,string>>(manifest)??throw new SetupException("Manifesto do pacote inválido.");
        var actual=Directory.GetFiles(directory,"*",SearchOption.AllDirectories).Select(p=>Path.GetRelativePath(directory,p).Replace('\\','/')).Where(p=>p!="package-manifest.json"&&p!="package-manifest.sig").ToArray();
        if(files.Count!=actual.Length||actual.Any(p=>!files.ContainsKey(p)))throw new SetupException("O pacote contém arquivos diferentes do manifesto assinado.");
        foreach(var name in actual){using var stream=File.OpenRead(Path.Combine(directory,name));if(!Convert.ToHexString(SHA256.HashData(stream)).Equals(files[name],StringComparison.OrdinalIgnoreCase))throw new SetupException("Um arquivo do agente difere do manifesto assinado.");}
    }
}