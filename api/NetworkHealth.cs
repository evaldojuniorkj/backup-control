using System.Net;
using System.Net.Sockets;
using System.Text.Json;
public record NetworkSnapshot(DateTimeOffset ObservedAt,string[] GlobalIpv6,string[] PreviousIpv6,DateTimeOffset? ChangedAt,string[] DnsIpv6,string[]? ManagedFtpIpv6=null);
public record NetworkAlert(string Code,string Message,DateTimeOffset? Since,string? Detail=null);
public sealed class NetworkHealth(StorageService storage,IConfiguration config){
 public async Task<List<NetworkAlert>> Alerts(){var path=config["NETWORK_STATUS_FILE"];if(string.IsNullOrEmpty(path))return [];try{var snapshot=JsonSerializer.Deserialize<NetworkSnapshot>(await File.ReadAllTextAsync(path),new JsonSerializerOptions{PropertyNameCaseInsensitive=true});return snapshot is null?Unavailable():Evaluate(snapshot,(await storage.Get())?.Host??"",DateTimeOffset.UtcNow);}catch(IOException){return Unavailable();}catch(JsonException){return Unavailable();}}
 static List<NetworkAlert> Unavailable()=>[new("network-monitor-stale","Verificação de IPv6 do servidor indisponível — não é possível confirmar mudanças de endereço",null)];
 public static List<NetworkAlert> Evaluate(NetworkSnapshot s,string ftpHost,DateTimeOffset now){
  if(now-s.ObservedAt>TimeSpan.FromMinutes(3)||s.ObservedAt>now.AddMinutes(1))return Unavailable();
  var alerts=new List<NetworkAlert>();var current=(s.GlobalIpv6??[]).Select(x=>IPAddress.TryParse(x,out var a)?a:null).Where(a=>a is not null).ToArray();
  if(current.Length==0){alerts.Add(new("server-ipv6-unavailable","Servidor sem endereço IPv6 global — acesso externo e envio FTP podem falhar",s.ObservedAt));return alerts;}
  if(IPAddress.TryParse(ftpHost,out var ftp)&&ftp.AddressFamily==AddressFamily.InterNetworkV6&&!current.Contains(ftp)&&((s.PreviousIpv6??[]).Concat(s.ManagedFtpIpv6??[]).Any(x=>IPAddress.TryParse(x,out var known)&&known.Equals(ftp)))){
   var changed=s.ChangedAt is not null&&(s.PreviousIpv6??[]).Any(x=>IPAddress.TryParse(x,out var old)&&old.Equals(ftp));
   alerts.Add(new(changed?"ftp-ipv6-changed":"ftp-ipv6-mismatch",changed?"IPv6 do servidor mudou e o destino FTP continua no endereço anterior":"IPv6 configurado no FTP não corresponde ao endereço atual deste servidor",s.ChangedAt??s.ObservedAt,"Envios podem falhar. Atualize o destino FTP em Configurações; a falha efetiva de envio aparece no resultado do cliente."));
  }
  if(!(s.DnsIpv6??[]).Any(x=>IPAddress.TryParse(x,out var dns)&&current.Contains(dns)))alerts.Add(new("central-ipv6-dns","DNS IPv6 da central não corresponde ao servidor — agentes podem perder comunicação",s.ObservedAt,"Confira o registro AAAA de backup.startxsistemas.com.br e a conectividade do provedor."));
  return alerts;
 }
}

