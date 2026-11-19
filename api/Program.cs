using System.Security.Claims;

using System.Security.Cryptography;

using System.Text;

using System.Text.RegularExpressions;

using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Authentication;

using Microsoft.AspNetCore.Authentication.Cookies;

using Microsoft.AspNetCore.DataProtection;

using Microsoft.AspNetCore.RateLimiting;

using Npgsql;

using Microsoft.AspNetCore.HttpOverrides;

using System.Net;



var builder=WebApplication.CreateBuilder(args);

var productionHttps=builder.Configuration.GetValue<bool>("PRODUCTION_HTTPS");

if(productionHttps){

    var proxies=(builder.Configuration["TRUSTED_PROXY_IPS"]??"").Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);

    if(proxies.Length==0)throw new Exception("Configure trusted proxy IPs for production HTTPS.");

    var addresses=proxies.Select(IPAddress.Parse).ToArray();

    builder.Services.Configure<ForwardedHeadersOptions>(o=>{o.ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto;o.ForwardLimit=2;o.KnownProxies.Clear();o.KnownIPNetworks.Clear();foreach(var address in addresses)o.KnownProxies.Add(address);});

}

builder.Services.AddSingleton(NpgsqlDataSource.Create(builder.Configuration.GetConnectionString("Database") ?? throw new Exception("Database required")));

builder.Services.AddSingleton<Database>();

builder.Services.AddSingleton<TeamUsers>();

builder.Services.AddSingleton<StorageService>();

builder.Services.AddSingleton<NetworkHealth>();

builder.Services.AddSingleton<FtpCatalog>();
builder.Services.AddSingleton<FtpTree>();
builder.Services.AddSingleton<ServerMonitor>();

builder.Services.AddSingleton<InstallationService>();

builder.Services.AddSingleton<RegistrationService>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<ClientDeletionService>();
builder.Services.AddSingleton<DeactivationService>();
builder.Services.AddHostedService<DeactivationWorker>();
builder.Services.AddSingleton<IClientDirectoryDeletion, FtpClientDirectoryDeletion>();

builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DATA_PROTECTION_KEYS_DIR"]??"/app/keys")).SetApplicationName("BackupControl");

builder.Services.AddAuthentication("team").AddCookie("team",o=>{

    o.Cookie.Name="backup-control-session";o.Cookie.HttpOnly=true;o.Cookie.SameSite=SameSiteMode.Strict;

    if(productionHttps)o.Cookie.SecurePolicy=CookieSecurePolicy.Always;

    o.ExpireTimeSpan=TimeSpan.FromHours(8);

    o.Events.OnValidatePrincipal=async c=>{if(!await c.HttpContext.RequestServices.GetRequiredService<TeamUsers>().Valid(c.Principal!)){c.RejectPrincipal();await c.HttpContext.SignOutAsync("team");}};

    o.Events.OnRedirectToLogin=c=>{c.Response.StatusCode=401;return Task.CompletedTask;};

    o.Events.OnRedirectToAccessDenied=c=>{c.Response.StatusCode=403;return Task.CompletedTask;};

});

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o=>{

    o.RejectionStatusCode=429;

    o.AddFixedWindowLimiter("enrollment", options=>{options.PermitLimit=120;options.Window=TimeSpan.FromMinutes(1);options.QueueLimit=0;});

    o.AddPolicy("login",ctx=>RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString()??"local",_=>new FixedWindowRateLimiterOptions{PermitLimit=10,Window=TimeSpan.FromMinutes(1),QueueLimit=0}));

});

var app=builder.Build();

if(productionHttps)app.UseForwardedHeaders();

var adminUser=builder.Configuration["ADMIN_USER"]??"admin";

var adminPassword=builder.Configuration["ADMIN_PASSWORD"]??"";

if(adminPassword.Length<16) throw new Exception("ADMIN_PASSWORD must contain at least 16 characters.");

await app.Services.GetRequiredService<Database>().Initialize();

await app.Services.GetRequiredService<TeamUsers>().Initialize(adminUser.Trim().ToLowerInvariant(),adminPassword);

app.Use(async(ctx,next)=>{

    try {await next();}

    catch(PostgresException e) when(e.SqlState=="23505") {ctx.Response.StatusCode=409;await ctx.Response.WriteAsJsonAsync(new{error="Já existe um cadastro ou comando ativo com esses dados."});}

    catch(ArgumentException e) {ctx.Response.StatusCode=400;await ctx.Response.WriteAsJsonAsync(new{error=e.Message});}

    catch(Exception e) {app.Logger.LogError("Request failed: {Type}",e.GetType().Name);ctx.Response.StatusCode=500;await ctx.Response.WriteAsJsonAsync(new{error="Não foi possível concluir a operação. Consulte a disponibilidade dos serviços."});}

});

app.UseRateLimiter();app.UseAuthentication();app.UseAuthorization();

app.Use(async(ctx,next)=>{

    if(ctx.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && !ctx.Request.Path.StartsWithSegments("/api/agent") && ctx.Request.Headers["X-Requested-With"]!="BackupControl")

    {ctx.Response.StatusCode=403;return;}

    await next();

});

app.MapGet("/api/health",async(Database db)=>{await db.Query("SELECT 1");return Results.Ok(new{status="healthy",service="Backup Control"});});

app.MapPost("/api/auth/login",async(TeamLogin input,HttpContext ctx,TeamUsers users)=>{

    var user=await users.Authenticate(input.Username,input.Password);if(user is null)return Results.Unauthorized();

    await ctx.SignInAsync("team",TeamUsers.Principal(user));return Results.Ok(new{username=user["username"],role=user["role"]});

}).RequireRateLimiting("login");

app.MapGet("/api/auth/me",(HttpContext ctx)=>Results.Ok(new{username=ctx.User.Identity!.Name,role=ctx.User.FindFirstValue(ClaimTypes.Role)})).RequireAuthorization();

app.MapPost("/api/auth/logout",async(HttpContext ctx)=>{await ctx.SignOutAsync("team");return Results.NoContent();}).RequireAuthorization();

app.MapPost("/api/auth/password",async(TeamPasswordInput input,HttpContext ctx,TeamUsers users)=>{var id=Guid.Parse(ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)!);if(!await users.Change(id,input))return Results.Json(new{error="Senha atual incorreta. Nenhuma alteração foi feita."},statusCode:400);await ctx.SignOutAsync("team");return Results.NoContent();}).RequireAuthorization().RequireRateLimiting("login");

app.MapGet("/api/users",async(TeamUsers users)=>Results.Ok(await users.List())).RequireAuthorization(p=>p.RequireRole("Admin"));

app.MapPost("/api/users",async(TeamUserInput input,HttpContext ctx,TeamUsers users)=>Results.Created("/api/users",new{id=await users.Create(input,ctx.User.Identity!.Name!)})).RequireAuthorization(p=>p.RequireRole("Admin")).RequireRateLimiting("login");

var api=app.MapGroup("/api").RequireAuthorization();

string hash(string value)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

string newToken()=>Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

bool validSlug(string s)=>s is not null && Regex.IsMatch(s,"^[a-z0-9][a-z0-9_-]{1,62}$");

void validateClient(ClientInput c){if(c.DifferentialIntervalHours<0||c.DifferentialIntervalHours>=c.ExpectedIntervalHours)throw new ArgumentException("O diferencial deve estar desativado (0) ou ter intervalo menor que o completo.");if(string.IsNullOrWhiteSpace(c.Name)||c.Name.Length>120||!validSlug(c.Slug)||string.IsNullOrWhiteSpace(c.Database)||c.Database.Length>128||c.ExpectedIntervalHours is <1 or >8760||c.GraceMinutes is <0 or >10080)throw new ArgumentException("Informe nome, banco e identificador com 2–63 letras minúsculas, números, hífen ou sublinhado. Intervalo: 1–8760 horas.");}

const string clientColumns="""

 c.id,c.name,c.slug,c.database,c.machine_name AS "machineName",c.enabled,c.agent_instance_id IS NOT NULL AS "hasAgentBinding",

 c.interval_hours AS "expectedIntervalHours",c.differential_interval_hours AS "differentialIntervalHours",c.grace_minutes AS "graceMinutes",

 c.last_seen AS "lastSeen",c.created_at AS "createdAt",

 c.remote_update_supported AS "remoteUpdateSupported",c.deletion_pending AS "deletionPending",
 c.deactivation_requested_at AS "deactivationRequestedAt",c.deactivation_confirmed_at AS "deactivationConfirmedAt",
 CASE WHEN c.deactivation_requested_at IS NULL THEN NULL WHEN c.deactivation_confirmed_at IS NOT NULL THEN 'Confirmed' WHEN c.remote_deactivation_supported OR c.agent_version ~ '^([1-9][0-9]*[.]|0[.]([9]|[1-9][0-9]+)[.])' OR c.remote_update_supported THEN 'WaitingForAgent' ELSE 'RequiresUpgrade' END AS "deactivationState",
 c.agent_telemetry AS "agentTelemetry",c.telemetry_received_at AS "telemetryReceivedAt",c.agent_version AS "agentVersion",

 jsonb_build_object('clockSkewSeconds',c.clock_skew_seconds,'clockSampledAt',c.clock_sampled_at,

 'pendingResults',c.pending_results,'lastResultError',c.last_result_error,'lastResultRejectedAt',c.last_result_rejected_at,

 'sqlRevision',(SELECT sql_revision::text FROM installation_settings WHERE id=1),

 'appliedSqlRevision',c.agent_telemetry->>'appliedSqlRevision','sqlConfigStatus',c.agent_telemetry->>'sqlConfigStatus',

 'sqlConfigError',c.agent_telemetry->>'sqlConfigError','requiresAgentUpdate',COALESCE(c.agent_version,'') !~ '^(0\.([5-9]|[1-9][0-9]+)\.|[1-9][0-9]*\.)') AS communication,

 (SELECT max(f.finished_at) FROM runs f WHERE f.client_id=c.id AND f.backup_type='Full' AND f.status='Succeeded') AS "lastFullBackupAt",

 (SELECT max(d.finished_at) FROM runs d WHERE d.client_id=c.id AND d.backup_type='Differential' AND d.status='Succeeded') AS "lastDifferentialBackupAt",

 (SELECT f.status FROM runs f WHERE f.client_id=c.id AND f.status<>'Skipped' AND f.backup_type='Full' ORDER BY f.finished_at DESC LIMIT 1) AS "lastFullStatus",

 (SELECT d.status FROM runs d WHERE d.client_id=c.id AND d.status<>'Skipped' AND d.backup_type='Differential' ORDER BY d.finished_at DESC LIMIT 1) AS "lastDifferentialStatus",

 (SELECT f.finished_at FROM runs f WHERE f.client_id=c.id AND f.status<>'Skipped' AND f.backup_type='Full' ORDER BY f.finished_at DESC LIMIT 1) AS "lastFullAttemptAt",

 (SELECT d.finished_at FROM runs d WHERE d.client_id=c.id AND d.status<>'Skipped' AND d.backup_type='Differential' ORDER BY d.finished_at DESC LIMIT 1) AS "lastDifferentialAttemptAt",

 (SELECT f.message FROM runs f WHERE f.client_id=c.id AND f.status<>'Skipped' AND f.backup_type='Full' ORDER BY f.finished_at DESC LIMIT 1) AS "lastFullMessage",

 (SELECT d.message FROM runs d WHERE d.client_id=c.id AND d.status<>'Skipped' AND d.backup_type='Differential' ORDER BY d.finished_at DESC LIMIT 1) AS "lastDifferentialMessage",

 (SELECT max(f.finished_at) FROM runs f WHERE f.client_id=c.id AND f.backup_type='Full' AND f.status IN ('Succeeded','Skipped')) AS "lastFullCheckAt",
 (SELECT max(f.finished_at) FROM runs f WHERE f.client_id=c.id AND f.backup_type IN ('Full','Differential') AND f.status IN ('Succeeded','Skipped')) AS "lastDifferentialCheckAt",
 (SELECT f.status FROM runs f WHERE f.client_id=c.id ORDER BY f.finished_at DESC LIMIT 1) AS "lastCheckStatus",
 (SELECT max(f.finished_at) FROM runs f WHERE f.client_id=c.id) AS "lastCheckAt",
 r.status AS "lastStatus",r.finished_at AS "lastBackupAt",r.size_bytes AS "lastSizeBytes",

 EXISTS(SELECT 1 FROM commands q WHERE q.client_id=c.id AND q.state IN ('Pending','Claimed')) AS "hasActiveCommand",

 CASE WHEN NOT c.enabled THEN 'Paused'

 WHEN COALESCE((SELECT max(f.finished_at) FROM runs f WHERE f.client_id=c.id AND f.backup_type='Full' AND f.status IN ('Succeeded','Skipped')),c.created_at)+make_interval(hours=>c.interval_hours,mins=>c.grace_minutes)<now() OR (c.differential_interval_hours>0 AND COALESCE((SELECT max(d.finished_at) FROM runs d WHERE d.client_id=c.id AND d.backup_type IN ('Full','Differential') AND d.status IN ('Succeeded','Skipped')),c.created_at)+make_interval(hours=>c.differential_interval_hours,mins=>c.grace_minutes)<now()) THEN 'Overdue'

 ELSE COALESCE(r.status,'NeverRun') END AS status

""";

api.MapGet("/clients",async(Database db,NetworkHealth network)=>{var rows=await db.Query("SELECT "+clientColumns+" FROM clients c LEFT JOIN LATERAL (SELECT * FROM runs WHERE client_id=c.id AND status<>'Skipped' ORDER BY finished_at DESC LIMIT 1) r ON true ORDER BY c.created_at DESC");var alerts=await network.Alerts();foreach(var row in rows)row["networkAlerts"]=alerts;return Results.Ok(rows);});

api.MapPost("/clients",async(ClientInput input,Database db,StorageService storage)=>{

    validateClient(input);var id=Guid.NewGuid();var token=newToken();

    await db.Execute("INSERT INTO clients(id,name,slug,database,interval_hours,grace_minutes,enabled,token_hash,differential_interval_hours) VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9)",id,input.Name.Trim(),input.Slug,input.Database.Trim(),input.ExpectedIntervalHours,input.GraceMinutes,input.Enabled,hash(token),input.DifferentialIntervalHours);

    await db.Execute("INSERT INTO audit(action,detail) VALUES('client.created',$1)",input.Slug);

    var s=await storage.Get();

    return Results.Created($"/api/clients/{id}",new{id,agentToken=token,remoteDirectory=(s?.BaseDirectory??"/backup")+"/"+input.Slug});

});

api.MapPut("/clients/{id:guid}",async(Guid id,ClientInput input,Database db)=>{

    validateClient(input);

    var existing=await db.Query("SELECT slug,enabled FROM clients WHERE id=$1 AND NOT deletion_pending",id);if(existing.Count==0)return Results.NotFound();

    if((string)existing[0]["slug"]! != input.Slug)throw new ArgumentException("O identificador é permanente para preservar o vínculo com o diretório.");

    await db.Execute("UPDATE clients SET name=$2,database=$3,interval_hours=$4,grace_minutes=$5,enabled=$6,differential_interval_hours=$7 WHERE id=$1 AND NOT deletion_pending",id,input.Name.Trim(),input.Database.Trim(),input.ExpectedIntervalHours,input.GraceMinutes,input.Enabled,input.DifferentialIntervalHours);

    if(!input.Enabled)await db.Execute("UPDATE commands SET state='Cancelled',finished_at=now() WHERE client_id=$1 AND state='Pending'",id);

    if((bool)existing[0]["enabled"]! != input.Enabled)await db.Execute("INSERT INTO audit(action,detail) VALUES($1,$2)",input.Enabled?"client.activated":"client.deactivated",id.ToString());

    return Results.NoContent();

});

api.MapDelete("/clients/{id:guid}",(Guid id,[Microsoft.AspNetCore.Mvc.FromBody] ClientDeletionInput input,ClientDeletionService service,CancellationToken ct)=>service.Delete(id,input,ct)).RequireAuthorization(p=>p.RequireRole("Admin"));
api.MapGet("/updates",(UpdateService service,Database db)=>service.List(db));
api.MapPost("/updates",(UpdateInput input,UpdateService service)=>service.Queue(input)).RequireAuthorization(p=>p.RequireRole("Admin"));
api.MapPost("/updates/{id:guid}/cancel",(Guid id,UpdateService service,Database db)=>service.Cancel(id,db)).RequireAuthorization(p=>p.RequireRole("Admin"));

api.MapPatch("/clients/{id:guid}/status",async(Guid id,ClientStatusInput input,Database db)=>{

    var rows=await db.Query("""

      WITH changed AS (UPDATE clients SET enabled=$2 WHERE id=$1 AND NOT deletion_pending RETURNING id,enabled),

      cancelled AS (UPDATE commands SET state='Cancelled',finished_at=now() WHERE client_id IN (SELECT id FROM changed) AND NOT $2 AND state='Pending' RETURNING id)

      SELECT id,enabled FROM changed

      """,id,input.Enabled);

    if(rows.Count==0)return Results.NotFound();

    await db.Execute("INSERT INTO audit(action,detail) VALUES($1,$2)",input.Enabled?"client.activated":"client.deactivated",id.ToString());

    return Results.Ok(rows[0]);

});

api.MapPost("/clients/{id:guid}/token",async(Guid id,Database db)=>{

    var token=newToken();var rows=await db.Query("UPDATE clients SET token_hash=$2 WHERE id=$1 RETURNING id",id,hash(token));

    return rows.Count==0?Results.NotFound():Results.Ok(new{agentToken=token});

});

api.MapGet("/server/metrics",async(ServerMonitor monitor,HttpContext ctx,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";var result=await monitor.Get(ct);return result is null?Results.Json(new{error="Monitor do servidor indisponível. Verifique o coletor no Linux; nenhuma métrica foi presumida."},statusCode:503):Results.Ok(result);});

api.MapGet("/storage",async(StorageService storage)=>{

    var s=await storage.Get();return Results.Ok(new{host=s?.Host??"",port=s?.Port??21,username=s?.Username??"",baseDirectory=s?.BaseDirectory??"/backup",hasPassword=s is not null});

});

api.MapPut("/storage",async(StorageInput input,StorageService storage)=>{

    if(string.IsNullOrWhiteSpace(input.Host)||input.Host.Length>253||input.Host.Contains('/')||string.IsNullOrWhiteSpace(input.Username)||input.Port is <1 or >65535||string.IsNullOrEmpty(input.BaseDirectory)||!Regex.IsMatch(input.BaseDirectory,"^(/[a-zA-Z0-9_-]+)+/?$"))throw new ArgumentException("Informe servidor sem protocolo, porta válida, usuário e diretório absoluto sem pontos ou espaços.");

    await storage.Save(input);return Results.NoContent();

});

async Task<IResult> testFtp(StorageService storage,string? slug,CancellationToken ct){try{return Results.Ok(await storage.Test(slug,ct));}catch(ArgumentException){throw;}catch{return Results.Json(new{error="Falha na conexão ou escrita FTP. Verifique credenciais, IPv6 e portas passivas."},statusCode:502);}}

api.MapPost("/storage/test",(StorageService s,CancellationToken ct)=>testFtp(s,null,ct));

api.MapPost("/clients/{id:guid}/test-ftp",async(Guid id,Database db,StorageService s,CancellationToken ct)=>{

    var rows=await db.Query("SELECT slug FROM clients WHERE id=$1",id);return rows.Count==0?Results.NotFound():await testFtp(s,(string)rows[0]["slug"]!,ct);

});

api.MapPost("/clients/{id:guid}/commands",async(Guid id,CommandInput input,Database db)=>{

    if(input.BackupType is not ("Full" or "Differential" or "Log"))throw new ArgumentException("Tipo de backup inválido.");

    var clients=await db.Query("SELECT enabled FROM clients WHERE id=$1",id);if(clients.Count==0)return Results.NotFound();

    if(!(bool)clients[0]["enabled"]!)throw new ArgumentException("O cliente está pausado.");

    var commandId=Guid.NewGuid();var inserted=await db.Query("WITH eligible AS (SELECT id FROM clients WHERE id=$2 AND enabled AND NOT deletion_pending FOR UPDATE) INSERT INTO commands(id,client_id,backup_type) SELECT $1,id,$3 FROM eligible RETURNING id",commandId,id,input.BackupType);
    if(inserted.Count==0)return Results.Conflict(new{error="Cliente pausado ou em exclusão."});

    await db.Execute("INSERT INTO audit(action,detail) VALUES('backup.requested',$1)",id+":"+input.BackupType);

    return Results.Created("/api/commands/"+commandId,new{id=commandId,state="Pending"});

});

api.MapGet("/commands",async(Database db)=>Results.Ok(await db.Query("SELECT q.id,q.client_id AS \"clientId\",c.name AS \"clientName\",q.backup_type AS \"backupType\",q.state,q.created_at AS \"createdAt\",q.claimed_at AS \"claimedAt\" FROM commands q JOIN clients c ON c.id=q.client_id ORDER BY q.created_at DESC LIMIT 200")));

api.MapPost("/commands/{id:guid}/cancel",async(Guid id,Database db)=>{

    var rows=await db.Query("UPDATE commands SET state='Cancelled',finished_at=now() WHERE id=$1 AND state='Pending' RETURNING id",id);

    return rows.Count==0?Results.Conflict(new{error="Somente comandos pendentes podem ser cancelados."}):Results.NoContent();

});

api.MapGet("/runs",async(Guid? clientId,Database db)=>Results.Ok(await db.Query("SELECT r.id,r.client_id AS \"clientId\",c.name AS \"clientName\",c.slug,r.backup_type AS \"backupType\",r.status,r.started_at AS \"startedAt\",r.finished_at AS \"finishedAt\",r.received_at AS \"receivedAt\",r.reported_started_at AS \"reportedStartedAt\",r.reported_finished_at AS \"reportedFinishedAt\",r.time_quality AS \"timeQuality\",r.clock_adjustment_seconds AS \"clockAdjustmentSeconds\",r.size_bytes AS \"sizeBytes\",r.file_name AS \"fileName\",r.message FROM runs r JOIN clients c ON c.id=r.client_id "+(clientId.HasValue?"WHERE r.client_id=$1 ":"")+"ORDER BY r.finished_at DESC LIMIT 300",clientId.HasValue?[clientId.Value]:[])));

api.MapGet("/audit",async(Database db)=>Results.Ok(await db.Query("SELECT action,detail,created_at AS \"createdAt\" FROM audit ORDER BY id DESC LIMIT 100")));

api.MapGet("/storage/tree",async(string? directory,FtpTree tree,HttpContext ctx,CancellationToken ct)=>{ctx.Response.Headers.CacheControl="no-store";try{return await tree.List(directory,ct);}catch(ArgumentException){throw;}catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}catch{return Results.Json(new{error="Não foi possível consultar a árvore do FTP. Confira a conexão e tente atualizar."},statusCode:502);}});
api.MapGet("/clients/{id:guid}/files",async(Guid id,FtpCatalog catalog,CancellationToken ct)=>{

    try{return await catalog.List(id,ct);}

    catch(ArgumentException){throw;}

    catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}

    catch{return Results.Json(new{error="Não foi possível consultar o FTP. Os arquivos não foram marcados como ausentes. Tente atualizar novamente."},statusCode:502);}

});

api.MapGet("/clients/{id:guid}/files/download",async(Guid id,string fileName,FtpCatalog catalog,HttpContext ctx)=>{

    try{await catalog.Download(id,fileName,ctx);}

    catch(ArgumentException){throw;}

    catch(OperationCanceledException) when(ctx.RequestAborted.IsCancellationRequested){ctx.Abort();}

    catch{if(ctx.Response.HasStarted){ctx.Abort();return;}ctx.Response.Clear();ctx.Response.StatusCode=502;await ctx.Response.WriteAsJsonAsync(new{error="Não foi possível baixar o arquivo do FTP. Tente novamente."});}

});

api.MapGet("/installation/settings",(InstallationService s)=>s.Get());

api.MapGet("/installer/download",(RegistrationService s,HttpContext ctx)=>s.Download(ctx));

api.MapGet("/registrations",(RegistrationService s,Database db)=>s.List(db));

api.MapPost("/registrations/{id:guid}/approve",(Guid id,RegistrationApproval input,RegistrationService s)=>s.Approve(id,input));

api.MapPost("/registrations/{id:guid}/reject",(Guid id,RegistrationService s,Database db)=>s.Reject(id,db));

api.MapPut("/installation/settings",(InstallationInput input,InstallationService s)=>s.Save(input));

api.MapPost("/clients/{id:guid}/installer",(Guid id,InstallationService s)=>s.Issue(id));

api.MapGet("/clients/{id:guid}/installers/{enrollmentId:guid}/download",(Guid id,Guid enrollmentId,InstallationService s,HttpContext ctx)=>s.Download(id,enrollmentId,ctx));

api.MapPost("/clients/{id:guid}/installers/{enrollmentId:guid}/revoke",(Guid id,Guid enrollmentId,InstallationService s)=>s.Revoke(id,enrollmentId));

app.MapPost("/api/agent/enrollment/redeem",(EnrollmentInput input,InstallationService s,HttpContext ctx)=>s.Redeem(input,ctx)).RequireRateLimiting("enrollment");

app.MapPost("/api/agent/registrations",(RegistrationInput input,RegistrationService s,HttpContext ctx)=>s.Register(input,ctx)).RequireRateLimiting("enrollment");

app.MapGet("/api/agent/registrations/{id:guid}",(Guid id,RegistrationService s,HttpContext ctx)=>s.Poll(id,ctx.Request.Headers["X-Registration-Secret"].ToString(),ctx));

var agent=app.MapGroup("/api/agent");

agent.AddEndpointFilter(async(ctx,next)=>{

    var auth=ctx.HttpContext.Request.Headers.Authorization.ToString();

    if(!auth.StartsWith("Bearer ",StringComparison.Ordinal)||auth.Length>200)return Results.Unauthorized();

    var db=ctx.HttpContext.RequestServices.GetRequiredService<Database>();

    var rows=await db.Query("SELECT * FROM clients WHERE token_hash=$1",hash(auth[7..]));

    var path=ctx.HttpContext.Request.Path.Value??"";
    var lifecycle=path=="/api/agent/lifecycle"||System.Text.RegularExpressions.Regex.IsMatch(path,@"^/api/agent/lifecycle/[0-9a-fA-F-]{36}/confirm$");
    var updateControl=path=="/api/agent/heartbeat"||path=="/api/agent/updates/current"||System.Text.RegularExpressions.Regex.IsMatch(path,@"^/api/agent/updates/[0-9a-fA-F-]{36}/status$")||System.Text.RegularExpressions.Regex.IsMatch(path,@"^/api/agent/package/[a-f0-9]{64}$");
    if(rows.Count==0&&lifecycle&&path.EndsWith("/confirm",StringComparison.Ordinal))rows=await db.Query("SELECT client_id AS id,instance_id AS agent_instance_id FROM deactivation_receipts WHERE token_hash=$1",hash(auth[7..]));
    if(rows.Count==0)return Results.Unauthorized();
    var live=rows[0].ContainsKey("enabled");
    if(!live&&!lifecycle)return Results.Unauthorized();

    if(!Guid.TryParse(ctx.HttpContext.Request.Headers["X-Agent-Instance"],out var instance)||instance==Guid.Empty)return Results.BadRequest(new{error="Identificação persistente da instalação ausente."});

    var bound=live?await db.Query("UPDATE clients SET agent_instance_id=$2 WHERE id=$1 AND (agent_instance_id=$2 OR (agent_instance_id IS NULL AND enabled AND NOT deletion_pending)) RETURNING *",rows[0]["id"],instance):rows.Where(x=>Equals(x["agent_instance_id"],instance)).ToList();

    if(bound.Count==0)return Results.Conflict(new{error="Cliente já vinculado a outra instalação. Não copie o token para outra máquina."});

    // Binding takes a row lock and returns fresh pause/decommission state. Never authorize from the pre-lock token lookup.
    var legacyDrain=live&&DeactivationService.LegacyReportDrain(bound[0])&&(path=="/api/agent/config"||path=="/api/agent/results");
    if(live&&(!(bool)bound[0]["enabled"]!||(bool)bound[0]["deletion_pending"]!)&&!lifecycle&&!legacyDrain&&!(updateControl&&(bool)bound[0]["deletion_pending"]!&&bound[0]["deactivation_confirmed_at"] is null))return Results.Unauthorized();
    ctx.HttpContext.Items["agent"]=bound[0];return await next(ctx);

});

Dictionary<string,object?> client(HttpContext ctx)=>(Dictionary<string,object?>)ctx.Items["agent"]!;

agent.MapPost("/heartbeat",async(Heartbeat input,HttpContext ctx,Database db,UpdateService updates)=>{

    if(string.IsNullOrWhiteSpace(input.MachineName)||input.MachineName.Length>120)throw new ArgumentException("Nome de máquina inválido.");

    if(input.AgentVersion?.Length>40)throw new ArgumentException("Versão inválida.");

    if(input.Telemetry is {} t && (t.AppliedFullIntervalHours is <1 or >8760 || t.AppliedDifferentialIntervalHours<0 || t.AppliedDifferentialIntervalHours>=t.AppliedFullIntervalHours || string.IsNullOrWhiteSpace(t.AppliedDatabase)||t.AppliedDatabase.Length>128 || t.CurrentOrigin is not (null or "Manual" or "Scheduled" or "Unknown") || t.CurrentBackupType is not (null or "Full" or "Differential" or "Log") || t.CurrentPhase is not (null or "Created" or "SqlStarted" or "BackupReady" or "ZipReady" or "Uploaded")))throw new ArgumentException("Telemetria de agenda inválida.");

    if(input.Telemetry is {} extra && (extra.PendingResultCount is <0 or >1000 || extra.LastResultError?.Length>4000 || extra.SqlConfigError?.Length>4000 || extra.AppliedSqlRevision?.Length>80 || extra.SqlConfigStatus is not (null or "Applied" or "Failed" or "Pending")))throw new ArgumentException("Diagnóstico de comunicação inválido.");

    var now=DateTimeOffset.UtcNow;

    var sample=ClockSamplePolicy.Select(input.Telemetry);

    var c=client(ctx);

    var sampleChanged=sample.HasValue&&(input.Telemetry?.AgentTime.HasValue==true||c["clock_agent_time"] is not DateTime previousSample||previousSample!=sample.Value.UtcDateTime);

    var safeTelemetry=input.Telemetry is null?null:input.Telemetry with{LastResultError=string.IsNullOrEmpty(input.Telemetry.LastResultError)?null:"Resultado pendente de confirmação pela central.",SqlConfigError=string.IsNullOrEmpty(input.Telemetry.SqlConfigError)?null:"A nova configuração SQL não foi validada; a configuração anterior foi preservada."};

    var telemetry=safeTelemetry is null?null:System.Text.Json.JsonSerializer.Serialize(safeTelemetry,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    await db.Execute("""

      UPDATE clients SET machine_name=$2,last_seen=now(),agent_version=COALESCE($3,agent_version),

      agent_telemetry=COALESCE($4::jsonb,agent_telemetry),telemetry_received_at=CASE WHEN $4::jsonb IS NULL THEN telemetry_received_at ELSE now() END,

      clock_skew_seconds=CASE WHEN $5 THEN $6 ELSE clock_skew_seconds END,

      clock_sampled_at=CASE WHEN $5 THEN now() ELSE clock_sampled_at END,

      clock_agent_time=CASE WHEN $5 THEN $7 ELSE clock_agent_time END,

      pending_results=COALESCE($8,pending_results),

      last_result_error=CASE WHEN $9 THEN $10 ELSE last_result_error END,
      remote_update_supported=COALESCE($11,remote_update_supported),remote_deactivation_supported=COALESCE($12,remote_deactivation_supported)

      WHERE id=$1

      """,c["id"],input.MachineName,(object?)input.AgentVersion??DBNull.Value,(object?)telemetry??DBNull.Value,

      sampleChanged,(object?)(sample.HasValue?(sample.Value-now).TotalSeconds:null)??DBNull.Value,(object?)sample?.UtcDateTime??DBNull.Value,

      (object?)safeTelemetry?.PendingResultCount??DBNull.Value,safeTelemetry?.PendingResultCount.HasValue==true,(object?)safeTelemetry?.LastResultError??DBNull.Value,(object?)input.RemoteUpdateSupported??DBNull.Value,(object?)input.RemoteDeactivationSupported??DBNull.Value);
 await updates.Reconcile((Guid)c["id"]!,db);

    return Results.Ok(new{serverTime=now});

});

agent.MapGet("/lifecycle",(HttpContext ctx,DeactivationService service,Database db)=>service.Lifecycle((Guid)client(ctx)["id"]!,db));
agent.MapPost("/lifecycle/{id:guid}/confirm",(Guid id,HttpContext ctx,DeactivationService service,Database db)=>service.Confirm((Guid)client(ctx)["id"]!,(Guid)client(ctx)["agent_instance_id"]!,id,db));

agent.MapGet("/updates/current",(HttpContext ctx,UpdateService service,Database db)=>service.Current((Guid)client(ctx)["id"]!,db));
agent.MapPost("/updates/{id:guid}/status",(Guid id,UpdateStatusInput input,HttpContext ctx,UpdateService service)=>service.Status((Guid)client(ctx)["id"]!,id,input));

agent.MapGet("/package/{digest}",(string digest,InstallationService s,HttpContext ctx)=>{ctx.Response.Headers.CacheControl="no-store";var path=s.SnapshotPath(digest);return path is null?Results.BadRequest():File.Exists(path)?Results.File(path,"application/zip","backup-control-agent-win-x64.zip"):Results.StatusCode(503);});

agent.MapGet("/package",(InstallationService s,HttpContext ctx)=>{ctx.Response.Headers.CacheControl="no-store";return File.Exists(s.Package)?Results.File(s.Package,"application/zip","backup-control-agent-win-x64.zip"):Results.StatusCode(503);});

agent.MapGet("/config",async(HttpContext ctx,StorageService storage,InstallationService installation)=>{

    var c=client(ctx);var s=await storage.Get();

    ctx.Response.Headers.CacheControl="no-store";

    var legacyDrain=DeactivationService.LegacyReportDrain(c);
    return Results.Ok(new{clientId=c["id"],slug=c["slug"],database=c["database"],expectedIntervalHours=c["interval_hours"],differentialIntervalHours=c["differential_interval_hours"],remoteDirectory=(s?.BaseDirectory??"/backup")+"/"+c["slug"],storage=legacyDrain||s is null?null:new{host=s.Host,port=s.Port,username=s.Username,password=storage.Password(s),protocol="FTP",passiveMode="EPSV"},sql=legacyDrain?null:await installation.RuntimeSql()});

});

agent.MapPost("/commands/claim",async(HttpContext ctx,Database db)=>{

    var rows=await db.Query("""

      WITH eligible AS (SELECT id FROM clients WHERE id=$1 AND enabled AND NOT deletion_pending FOR UPDATE)
      UPDATE commands SET state='Claimed',claimed_at=now(),claimed_by=$2 WHERE id=(

      SELECT id FROM commands WHERE client_id IN (SELECT id FROM eligible) AND state='Pending' ORDER BY created_at FOR UPDATE SKIP LOCKED LIMIT 1)

      RETURNING id,backup_type AS "backupType",created_at AS "createdAt"

      """,client(ctx)["id"],client(ctx)["agent_instance_id"]);

    return rows.Count==0?Results.NoContent():Results.Ok(rows[0]);

});

agent.MapGet("/commands/active",async(HttpContext ctx,Database db)=>{

    var rows=await db.Query("SELECT id,backup_type AS \"backupType\",created_at AS \"createdAt\" FROM commands WHERE client_id=$1 AND claimed_by=$2 AND state='Claimed' ORDER BY claimed_at LIMIT 1",client(ctx)["id"],client(ctx)["agent_instance_id"]);

    return rows.Count==0?Results.NoContent():Results.Ok(rows[0]);

});

agent.MapPost("/results",async(RunInput input,HttpContext ctx,Database db)=>{

    var c=client(ctx);var cid=c["id"];

    async Task<IResult> Reject(string message,int status=400){await db.Execute("UPDATE clients SET last_result_error=$2,last_result_rejected_at=now(),pending_results=GREATEST(COALESCE(pending_results,0),1) WHERE id=$1",cid,message);return Results.Json(new{error=message},statusCode:status);}

    if(!RunPolicy.Valid(input))return await Reject("Resultado inválido: confira os campos e o nome do arquivo, sem diretórios.");

    if(input.CommandId.HasValue){var own=await db.Query("SELECT id,backup_type FROM commands WHERE id=$1 AND client_id=$2 AND state IN ('Claimed','Completed')",input.CommandId.Value,cid);if(own.Count==0)return await Reject("Resultado não corresponde a um comando atribuído a este agente.",404);if((string)own[0]["backup_type"]! != input.BackupType)return await Reject("Tipo de backup diferente da solicitação.");}

    var now=DateTimeOffset.UtcNow;

    var skew=c["clock_skew_seconds"] is double offset?offset:(double?)null;

    var sampled=c["clock_sampled_at"] is DateTime sample?new DateTimeOffset(DateTime.SpecifyKind(sample,DateTimeKind.Utc)):(DateTimeOffset?)null;

    var time=ResultTimePolicy.Resolve(input.StartedAt,input.FinishedAt,now,skew,sampled);

    var rows=await db.Query("""

      WITH inserted AS (INSERT INTO runs(id,client_id,event_id,command_id,backup_type,status,started_at,finished_at,size_bytes,file_name,message,reported_started_at,reported_finished_at,time_quality,clock_adjustment_seconds)

      VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15) ON CONFLICT(client_id,event_id) DO NOTHING RETURNING id,command_id),

      completed AS (UPDATE commands SET state='Completed',finished_at=now() WHERE id IN (SELECT command_id FROM inserted)),

      acknowledged AS (UPDATE clients SET last_result_error=NULL,pending_results=CASE WHEN pending_results IS NULL THEN 0 ELSE GREATEST(0,pending_results-1) END WHERE id=$2 AND EXISTS(SELECT 1 FROM inserted)) SELECT id FROM inserted

      """,Guid.NewGuid(),cid,input.EventId,input.CommandId,input.BackupType,input.Status,time.StartedAt.UtcDateTime,time.FinishedAt.UtcDateTime,input.SizeBytes,input.FileName,input.Message,input.StartedAt.UtcDateTime,input.FinishedAt.UtcDateTime,time.Quality,(object?)time.AdjustmentSeconds??DBNull.Value);

    return rows.Count==0?Results.Ok(new{duplicate=true}):Results.Created("/api/runs/"+rows[0]["id"],new{id=rows[0]["id"],timeQuality=time.Quality});

});

app.Run();







