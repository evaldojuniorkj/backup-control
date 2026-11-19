using System.Security.Cryptography;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

public static class TeamPasswords {
 public static void Validate(string password){if(string.IsNullOrWhiteSpace(password)||password.Length<8||password.Length>256)throw new ArgumentException("Use uma senha de 8 a 256 caracteres.");}
 public static string Hash(string password){Validate(password);var salt=RandomNumberGenerator.GetBytes(16);var digest=Rfc2898DeriveBytes.Pbkdf2(password,salt,600000,HashAlgorithmName.SHA256,32);return "v1:"+Convert.ToBase64String(salt)+":"+Convert.ToBase64String(digest);}
 public static bool Verify(string password,string encoded){try{if(password is null||password.Length>256)return false;var parts=encoded.Split(':');if(parts.Length!=3||parts[0]!="v1")return false;var salt=Convert.FromBase64String(parts[1]);var expected=Convert.FromBase64String(parts[2]);if(salt.Length!=16||expected.Length!=32)return false;return CryptographicOperations.FixedTimeEquals(expected,Rfc2898DeriveBytes.Pbkdf2(password,salt,600000,HashAlgorithmName.SHA256,32));}catch(FormatException){return false;}}
}
public record TeamLogin(string Username,string Password);
public record TeamUserInput(string Username,string Password,string Role);
public record TeamPasswordInput(string CurrentPassword,string NewPassword);
public sealed class TeamUsers(Database db){
 public async Task Initialize(string username,string password){
  await db.Execute("CREATE TABLE IF NOT EXISTS team_users(id uuid PRIMARY KEY,username text NOT NULL UNIQUE,password_hash text NOT NULL,role text NOT NULL CHECK(role IN ('Admin','Operator')),session_version uuid NOT NULL,created_at timestamptz NOT NULL DEFAULT now())");
  // An installation is bootstrapped once. Environment credentials never reset a changed account.
  await db.Execute("INSERT INTO team_users(id,username,password_hash,role,session_version) SELECT $1,$2,$3,'Admin',$4 WHERE NOT EXISTS(SELECT 1 FROM team_users) ON CONFLICT DO NOTHING",Guid.NewGuid(),username,TeamPasswords.Hash(password),Guid.NewGuid());
 }
 public async Task<Dictionary<string,object?>?> Authenticate(string username,string password){var rows=await db.Query("SELECT * FROM team_users WHERE username=$1",(username??"").Trim().ToLowerInvariant());var encoded=rows.Count==0?DummyHash:(string)rows[0]["password_hash"]!;return TeamPasswords.Verify(password,encoded)&&rows.Count>0?rows[0]:null;}
 private static readonly string DummyHash=TeamPasswords.Hash("Invalid-account-dummy-password");
 public static ClaimsPrincipal Principal(Dictionary<string,object?> row)=>new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,row["id"]!.ToString()!),new Claim(ClaimTypes.Name,(string)row["username"]!),new Claim(ClaimTypes.Role,(string)row["role"]!),new Claim("session_version",row["session_version"]!.ToString()!)],"team"));
 public async Task<bool> Valid(ClaimsPrincipal principal){if(!Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier),out var id)||!Guid.TryParse(principal.FindFirstValue("session_version"),out var version))return false;var rows=await db.Query("SELECT id FROM team_users WHERE id=$1 AND session_version=$2",id,version);return rows.Count==1;}
 public async Task<List<Dictionary<string,object?>>> List()=>await db.Query("SELECT id,username,role,created_at AS \"createdAt\" FROM team_users ORDER BY created_at");
 public async Task<Guid> Create(TeamUserInput input,string actor){var name=(input.Username??"").Trim().ToLowerInvariant();if(!System.Text.RegularExpressions.Regex.IsMatch(name,"^[a-z0-9][a-z0-9._-]{2,63}$")||input.Role is not ("Admin" or "Operator"))throw new ArgumentException("Use um usuário de 3 a 64 letras minúsculas/números e um perfil válido.");var id=Guid.NewGuid();await db.Execute("INSERT INTO team_users(id,username,password_hash,role,session_version) VALUES($1,$2,$3,$4,$5)",id,name,TeamPasswords.Hash(input.Password),input.Role,Guid.NewGuid());await db.Execute("INSERT INTO audit(action,detail) VALUES('user.created',$1)",actor+":"+name);return id;}
 public async Task<bool> Change(Guid id,TeamPasswordInput input){TeamPasswords.Validate(input.NewPassword);var rows=await db.Query("SELECT password_hash FROM team_users WHERE id=$1",id);if(rows.Count!=1||!TeamPasswords.Verify(input.CurrentPassword,(string)rows[0]["password_hash"]!))return false;var changed=await db.Query("UPDATE team_users SET password_hash=$2,session_version=$3 WHERE id=$1 AND password_hash=$4 RETURNING id",id,TeamPasswords.Hash(input.NewPassword),Guid.NewGuid(),rows[0]["password_hash"]);if(changed.Count!=1)return false;await db.Execute("INSERT INTO audit(action,detail) VALUES('user.password_changed',$1)",id.ToString());return true;}
}
