using FluentFTP;
public static class FtpTreePolicy {
 public static bool SafeName(string name)=>!string.IsNullOrWhiteSpace(name)&&name.Length<=240&&name is not ("." or "..")&&!name.Contains('/')&&!name.Contains('\\')&&!name.Any(char.IsControl);
 public static string Resolve(string root,string? relative){var parts=string.IsNullOrEmpty(relative)?Array.Empty<string>():relative.Split('/');if(parts.Length>8||parts.Any(p=>!SafeName(p)))throw new ArgumentException("Diretório inválido. A navegação está restrita à pasta de backups.");return root.TrimEnd('/')+(parts.Length==0?"":"/"+string.Join('/',parts));}
}
public sealed class FtpTree(StorageService storage){
 public async Task<IResult> List(string? directory,CancellationToken ct){
  var s=await storage.Get()??throw new ArgumentException("Configure o armazenamento FTP primeiro.");var path=FtpTreePolicy.Resolve(s.BaseDirectory,directory);
  using var ftp=new AsyncFtpClient(s.Host,s.Username,storage.Password(s),s.Port);
  ftp.Config.ConnectTimeout=10000;ftp.Config.ReadTimeout=20000;ftp.Config.DataConnectionConnectTimeout=10000;ftp.Config.DataConnectionReadTimeout=20000;ftp.Config.DataConnectionType=FtpDataConnectionType.EPSV;ftp.Config.TimeConversion=FtpDate.UTC;
  await ftp.Connect(ct);
  var cursor=s.BaseDirectory.TrimEnd('/');
  if(!await ftp.DirectoryExists(cursor,ct))return Results.Ok(new{directory=cursor,relativeDirectory="",exists=false,checkedAt=DateTimeOffset.UtcNow,entries=Array.Empty<object>(),truncated=false});
  // Walk each segment from trusted root, refusing links before listing descendants.
  foreach(var segment in (directory??"").Split('/',StringSplitOptions.RemoveEmptyEntries)){
   var child=(await ftp.GetListing(cursor,FtpListOption.Size,ct)).FirstOrDefault(x=>x.Name==segment);
   if(child is null||child.Type!=FtpObjectType.Directory)throw new ArgumentException("A pasta não existe ou é um link. Atualize a árvore.");cursor+="/"+segment;
  }
  var items=(await ftp.GetListing(path,FtpListOption.Modify|FtpListOption.Size,ct)).Where(x=>FtpTreePolicy.SafeName(x.Name)).OrderBy(x=>x.Type==FtpObjectType.Directory?0:1).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ToArray();
  var entries=items.Take(1000).Select(x=>new{name=x.Name,kind=x.Type==FtpObjectType.Directory?"directory":x.Type==FtpObjectType.File?"file":"link",relativeDirectory=string.IsNullOrEmpty(directory)?x.Name:directory+"/"+x.Name,sizeBytes=x.Type==FtpObjectType.File&&x.Size>=0?(long?)x.Size:null,modifiedAt=x.Modified==DateTime.MinValue?(DateTimeOffset?)null:new DateTimeOffset(DateTime.SpecifyKind(x.Modified,DateTimeKind.Utc))}).ToArray();
  return Results.Ok(new{directory=path,relativeDirectory=directory??"",exists=true,checkedAt=DateTimeOffset.UtcNow,entries,truncated=items.Length>1000});
 }
}
