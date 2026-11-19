using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

public sealed class DisposableDatabaseFactAttribute:FactAttribute
{
    public DisposableDatabaseFactAttribute(){if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("API_TEST_DATABASE")))Skip="Set API_TEST_DATABASE to a disposable PostgreSQL database to run HTTP/DB integration tests.";}
}
public class UpdatesIntegrationTests
{
    [DisposableDatabaseFact]
    public async Task Http_queue_ownership_status_confirmation_and_role_authorization()
    {
        await using var f=await Fixture.Start();
        var a=await f.Create("0.7.0",true);var b=await f.Create("0.6.0",false);var other=await f.Create("0.7.0",true);
        var queue=await f.Admin.PostAsJsonAsync("/api/updates",new{clientIds=new[]{a.Id,b.Id}});queue.EnsureSuccessStatusCode();
        var result=await queue.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(result.GetProperty("accepted").EnumerateArray());Assert.Single(result.GetProperty("rejected").EnumerateArray());
        var job=result.GetProperty("accepted")[0].GetProperty("id").GetGuid();
        var duplicate=await (await f.Admin.PostAsJsonAsync("/api/updates",new{clientIds=new[]{a.Id}})).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(duplicate.GetProperty("accepted").EnumerateArray());
        var current=await a.Http.GetFromJsonAsync<JsonElement>("/api/agent/updates/current");
        Assert.Equal(job,current.GetProperty("id").GetGuid());Assert.Equal("https://central.example/api/agent/package/"+f.Digest,current.GetProperty("url").GetString());
        var again=await a.Http.GetFromJsonAsync<JsonElement>("/api/agent/updates/current");Assert.Equal(job,again.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound,(await other.Http.PostAsJsonAsync($"/api/agent/updates/{job}/status",new{state="Downloading"})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await a.Http.PostAsJsonAsync($"/api/agent/updates/{job}/status",new{state="Applying"})).StatusCode);
        foreach(var state in new[]{"Waiting","Downloading","Applying","Succeeded"})(await a.Http.PostAsJsonAsync($"/api/agent/updates/{job}/status",new{state,message="password=SHOULD-NOT-PERSIST"})).EnsureSuccessStatusCode();
        var rows=await f.Db.Query("SELECT state,message FROM agent_updates WHERE id=$1",job);
        Assert.Equal("Verifying",rows[0]["state"]);Assert.DoesNotContain("SHOULD",(string)rows[0]["message"]!);
        (await a.Http.PostAsJsonAsync("/api/agent/heartbeat",new{machineName="fixture",agentVersion="0.8.0",remoteUpdateSupported=true})).EnsureSuccessStatusCode();
        Assert.Equal("Succeeded",(await f.Db.Query("SELECT state FROM agent_updates WHERE id=$1",job))[0]["state"]);
        Assert.Equal(HttpStatusCode.NoContent,(await a.Http.GetAsync("/api/agent/updates/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await a.Http.PostAsJsonAsync($"/api/agent/updates/{job}/status",new{state="Failed"})).StatusCode);
        (await f.Admin.PostAsJsonAsync("/api/users",new{username="operator",password="operator-test-password",role="Operator"})).EnsureSuccessStatusCode();
        using var op=f.Http();(await op.PostAsJsonAsync("/api/auth/login",new{username="operator",password="operator-test-password"})).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden,(await op.PostAsJsonAsync("/api/updates",new{clientIds=new[]{other.Id}})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await op.SendAsync(new(HttpMethod.Delete,$"/api/clients/{other.Id}"){Content=JsonContent.Create(new{confirmationSlug=other.Slug,agentStopped=true})})).StatusCode);
        var package=await a.Http.GetAsync("/api/agent/package/"+f.Digest);package.EnsureSuccessStatusCode();Assert.Equal(f.Package,await package.Content.ReadAsByteArrayAsync());
    }

    [DisposableDatabaseFact]
    public async Task Skipped_results_keep_real_success_and_refresh_due_assessment()
    {
        await using var f=await Fixture.Start();var a=await f.Create("0.8.0",true);
        await f.Db.Execute("UPDATE clients SET created_at=now()-interval '4 days' WHERE id=$1",a.Id);
        var old=DateTimeOffset.UtcNow.AddDays(-2);
        await f.Db.Execute("INSERT INTO runs(id,client_id,event_id,backup_type,status,started_at,finished_at,size_bytes,file_name,message) VALUES($1,$2,$3,'Full','Succeeded',$4,$4,456,'old.zip','ok')",Guid.NewGuid(),a.Id,Guid.NewGuid(),old);
        var now=DateTimeOffset.UtcNow;
        var skipped=new RunInput(Guid.NewGuid(),null,"Full","Skipped",now,now,0,"","Sem alterações no banco.");
        Assert.Equal(HttpStatusCode.BadRequest,(await a.Http.PostAsJsonAsync("/api/agent/results",skipped with{FileName="bad.zip"})).StatusCode);
        Assert.Equal(HttpStatusCode.Created,(await a.Http.PostAsJsonAsync("/api/agent/results",skipped)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await a.Http.PostAsJsonAsync("/api/agent/results",skipped)).StatusCode);
        var clients=await f.Admin.GetFromJsonAsync<JsonElement>("/api/clients");var client=clients.EnumerateArray().Single(x=>x.GetProperty("id").GetGuid()==a.Id);
        Assert.Equal("Succeeded",client.GetProperty("status").GetString());Assert.Equal("Succeeded",client.GetProperty("lastStatus").GetString());Assert.Equal("Skipped",client.GetProperty("lastCheckStatus").GetString());
        Assert.Equal(old.ToUnixTimeSeconds(),client.GetProperty("lastFullBackupAt").GetDateTimeOffset().ToUnixTimeSeconds());Assert.True(client.GetProperty("lastFullCheckAt").GetDateTimeOffset()>old);
        Assert.Equal(456,client.GetProperty("lastSizeBytes").GetInt64());
        Assert.Equal(2L,(await f.Db.Query("SELECT count(*) AS n FROM runs WHERE client_id=$1",a.Id))[0]["n"]);
    }

    [DisposableDatabaseFact]
    public async Task Concurrent_queue_is_unique_and_cancellation_only_allows_pending_or_waiting()
    {
        await using var f=await Fixture.Start();var a=await f.Create("0.7.0",true);
        var queued=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>f.Admin.PostAsJsonAsync("/api/updates",new{clientIds=new[]{a.Id}})));
        foreach(var response in queued)response.EnsureSuccessStatusCode();
        var jobs=await f.Db.Query("SELECT id FROM agent_updates WHERE client_id=$1",a.Id);Assert.Single(jobs);var id=(Guid)jobs[0]["id"]!;
        Assert.Equal(HttpStatusCode.NoContent,(await f.Admin.PostAsync($"/api/updates/{id}/cancel",null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await a.Http.GetAsync("/api/agent/updates/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await a.Http.PostAsJsonAsync($"/api/agent/updates/{id}/status",new{state="Downloading"})).StatusCode);
        var response2=await (await f.Admin.PostAsJsonAsync("/api/updates",new{clientIds=new[]{a.Id}})).Content.ReadFromJsonAsync<JsonElement>();id=response2.GetProperty("accepted")[0].GetProperty("id").GetGuid();
        (await a.Http.PostAsJsonAsync($"/api/agent/updates/{id}/status",new{state="Downloading"})).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict,(await f.Admin.PostAsync($"/api/updates/{id}/cancel",null)).StatusCode);
        var other=await f.Create("0.7.0",true);
        Assert.Equal(HttpStatusCode.NotFound,(await other.Http.PostAsJsonAsync($"/api/agent/updates/{id}/status",new{state="RolledBack"})).StatusCode);
        (await a.Http.PostAsJsonAsync($"/api/agent/updates/{id}/status",new{state="RolledBack"})).EnsureSuccessStatusCode();
        Assert.Equal("RolledBack",(await f.Db.Query("SELECT state FROM agent_updates WHERE id=$1",id))[0]["state"]);
        Assert.Equal(HttpStatusCode.Conflict,(await a.Http.PostAsJsonAsync($"/api/agent/updates/{id}/status",new{state="Applying"})).StatusCode);
        await File.WriteAllTextAsync(Path.Combine(f.DirectoryPath,"release.json"),"{\"version\":\"0.8.0\",\"sha256\":\"bad\"}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await f.Admin.PostAsJsonAsync("/api/updates",new{clientIds=new[]{a.Id}})).StatusCode);
    }

    [DisposableDatabaseFact]
    public async Task Active_client_deletion_is_rejected_without_changing_data_or_touching_ftp()
    {
        await using var f=await Fixture.Start();var a=await f.Create("0.8.0",true);
        var storage=new StorageService(f.Db,new EphemeralDataProtectionProvider());await storage.Save(new("fixture.invalid",21,"disposable","secret","/backup"));
        var fake=new DeleteFixture{Fail=false};var deletion=new ClientDeletionService(f.Source,storage,fake);
        var result=await deletion.Delete(a.Id,new(a.Slug,true),default);
        Assert.Equal(409,((IStatusCodeHttpResult)result).StatusCode);Assert.Equal(0,fake.Calls);
        var row=(await f.Db.Query("SELECT enabled,deletion_pending FROM clients WHERE id=$1",a.Id))[0];
        Assert.Equal(true,row["enabled"]);Assert.Equal(false,row["deletion_pending"]);
        Assert.Empty(await f.Db.Query("SELECT id FROM audit WHERE action='client.deleted'"));
    }

    [DisposableDatabaseFact]
    public async Task Bound_deletion_requires_instance_ack_and_preserves_history_until_ftp_success()
    {
        await using var f=await Fixture.Start();var a=await f.Create("0.9.0",true);var other=await f.Create("0.9.0",true);
        await f.Db.Execute("UPDATE clients SET enabled=false WHERE id=$1",a.Id);
        var storage=new StorageService(f.Db,new EphemeralDataProtectionProvider());await storage.Save(new("fixture.invalid",21,"disposable","secret","/backup"));
        var ftp=new DeleteFixture();var deletion=new ClientDeletionService(f.Source,storage,ftp);
        // Neither the old checkbox nor stale telemetry is evidence of permanent local sanitization.
        await f.Db.Execute("UPDATE clients SET last_seen=now()-interval '2 days',agent_telemetry='{\"currentPhase\":\"SqlStarted\"}'::jsonb WHERE id=$1",a.Id);
        Assert.Equal(202,((IStatusCodeHttpResult)await deletion.Delete(a.Id,new(a.Slug,true),default)).StatusCode);
        Assert.Equal(202,((IStatusCodeHttpResult)await deletion.Delete(a.Id,new(a.Slug,false),default)).StatusCode);
        Assert.Equal(409,((IStatusCodeHttpResult)await deletion.Finalize(a.Id,default)).StatusCode);Assert.Equal(0,ftp.Calls);
        Assert.Equal(HttpStatusCode.Unauthorized,(await a.Http.GetAsync("/api/agent/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await a.Http.PostAsJsonAsync("/api/agent/commands/claim",new{})).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await f.Admin.PatchAsJsonAsync($"/api/clients/{a.Id}/status",new{enabled=true})).StatusCode);
        var lifecycle=await a.Http.GetFromJsonAsync<JsonElement>("/api/agent/lifecycle");var id=lifecycle.GetProperty("id").GetGuid();Assert.True(lifecycle.GetProperty("ready").GetBoolean());
        Assert.Equal(id,(await a.Http.GetFromJsonAsync<JsonElement>("/api/agent/lifecycle")).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound,(await other.Http.PostAsJsonAsync($"/api/agent/lifecycle/{id}/confirm",new{})).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await a.Http.PostAsJsonAsync($"/api/agent/lifecycle/{Guid.NewGuid()}/confirm",new{})).StatusCode);
        (await a.Http.PostAsJsonAsync($"/api/agent/lifecycle/{id}/confirm",new{})).EnsureSuccessStatusCode();
        Assert.Single(await f.Db.Query("SELECT id FROM clients WHERE id=$1",a.Id));
        Assert.Equal(502,((IStatusCodeHttpResult)await deletion.Finalize(a.Id,default)).StatusCode);
        Assert.Single(await f.Db.Query("SELECT id FROM clients WHERE id=$1",a.Id));
        ftp.Fail=false;Assert.Equal(204,((IStatusCodeHttpResult)await deletion.Finalize(a.Id,default)).StatusCode);
        Assert.Empty(await f.Db.Query("SELECT id FROM clients WHERE id=$1",a.Id));
        Assert.Equal(HttpStatusCode.NoContent,(await a.Http.PostAsJsonAsync($"/api/agent/lifecycle/{id}/confirm",new{})).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await a.Http.GetAsync("/api/agent/config")).StatusCode);
    }

    [DisposableDatabaseFact]
    public async Task Paused_helper_upgrades_without_credentials_and_ack_waits_for_version_heartbeat()
    {
        await using var f=await Fixture.Start();var a=await f.Create("0.8.0",true);await f.Db.Execute("UPDATE clients SET enabled=false WHERE id=$1",a.Id);
        await File.WriteAllTextAsync(Path.Combine(f.DirectoryPath,"release.json"),JsonSerializer.Serialize(new{version="0.9.0",sha256=f.Digest}));
        var response=await f.Admin.SendAsync(new(HttpMethod.Delete,$"/api/clients/{a.Id}"){Content=JsonContent.Create(new{confirmationSlug=a.Slug})});Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var life=await a.Http.GetFromJsonAsync<JsonElement>("/api/agent/lifecycle");var id=life.GetProperty("id").GetGuid();Assert.False(life.GetProperty("ready").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict,(await a.Http.PostAsJsonAsync($"/api/agent/lifecycle/{id}/confirm",new{})).StatusCode);
        var job=(await a.Http.GetFromJsonAsync<JsonElement>("/api/agent/updates/current")).GetProperty("id").GetGuid();
        var drain=await a.Http.GetFromJsonAsync<JsonElement>("/api/agent/config");Assert.Equal(JsonValueKind.Null,drain.GetProperty("storage").ValueKind);Assert.Equal(JsonValueKind.Null,drain.GetProperty("sql").ValueKind);
        (await a.Http.GetAsync("/api/agent/package/"+f.Digest)).EnsureSuccessStatusCode();
        (await a.Http.PostAsJsonAsync($"/api/agent/updates/{job}/status",new{state="Failed"})).EnsureSuccessStatusCode();
        await a.Http.GetAsync("/api/agent/lifecycle");Assert.Equal(HttpStatusCode.NoContent,(await a.Http.GetAsync("/api/agent/updates/current")).StatusCode);
        Assert.Single(await f.Db.Query("SELECT id FROM agent_updates WHERE client_id=$1",a.Id));
        Assert.Equal(HttpStatusCode.Accepted,(await f.Admin.SendAsync(new(HttpMethod.Delete,$"/api/clients/{a.Id}"){Content=JsonContent.Create(new{confirmationSlug=a.Slug})})).StatusCode);
        await a.Http.GetAsync("/api/agent/lifecycle");
        var retried=(await a.Http.GetFromJsonAsync<JsonElement>("/api/agent/updates/current")).GetProperty("id").GetGuid();Assert.NotEqual(job,retried);job=retried;
        foreach(var state in new[]{"Downloading","Applying","Verifying"})(await a.Http.PostAsJsonAsync($"/api/agent/updates/{job}/status",new{state})).EnsureSuccessStatusCode();
        (await a.Http.PostAsJsonAsync("/api/agent/heartbeat",new{machineName="fixture",agentVersion="0.9.0",remoteDeactivationSupported=true})).EnsureSuccessStatusCode();
        Assert.True((await a.Http.GetFromJsonAsync<JsonElement>("/api/agent/lifecycle")).GetProperty("ready").GetBoolean());
        Assert.Equal("Succeeded",(await f.Db.Query("SELECT state FROM agent_updates WHERE id=$1",job))[0]["state"]);
        var clients=await f.Admin.GetFromJsonAsync<JsonElement>("/api/clients");Assert.Equal("WaitingForAgent",clients.EnumerateArray().Single(x=>x.GetProperty("id").GetGuid()==a.Id).GetProperty("deactivationState").GetString());
    }

    [DisposableDatabaseFact]
    public async Task Never_bound_client_deletes_directly_and_old_agent_without_helper_requires_upgrade()
    {
        await using var f=await Fixture.Start();var slug="never-bound-"+Guid.NewGuid().ToString("N");
        var created=await (await f.Admin.PostAsJsonAsync("/api/clients",new{name=slug,slug,database="fixture",expectedIntervalHours=24})).Content.ReadFromJsonAsync<JsonElement>();var id=created.GetProperty("id").GetGuid();
        await f.Db.Execute("UPDATE clients SET enabled=false WHERE id=$1",id);
        var storage=new StorageService(f.Db,new EphemeralDataProtectionProvider());await storage.Save(new("fixture.invalid",21,"disposable","secret","/backup"));
        var ftp=new DeleteFixture{Fail=false};var deletion=new ClientDeletionService(f.Source,storage,ftp);
        await Assert.ThrowsAsync<ArgumentException>(()=>deletion.Delete(id,new("wrong"),default));Assert.Equal(0,ftp.Calls);
        Assert.Equal(204,((IStatusCodeHttpResult)await deletion.Delete(id,new(slug),default)).StatusCode);Assert.Equal(1,ftp.Calls);
        var old=await f.Create("0.8.0",false);await f.Db.Execute("UPDATE clients SET enabled=false WHERE id=$1",old.Id);
        var result=await f.Admin.SendAsync(new(HttpMethod.Delete,$"/api/clients/{old.Id}"){Content=JsonContent.Create(new{confirmationSlug=old.Slug,agentStopped=true})});
        Assert.Equal(HttpStatusCode.Accepted,result.StatusCode);Assert.Equal("RequiresUpgrade",(await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Equal(1,ftp.Calls);Assert.Single(await f.Db.Query("SELECT id FROM clients WHERE id=$1",old.Id));
        using var impostor=f.Http();impostor.DefaultRequestHeaders.Authorization=old.Http.DefaultRequestHeaders.Authorization;impostor.DefaultRequestHeaders.Add("X-Agent-Instance",Guid.NewGuid().ToString());
        Assert.Equal(HttpStatusCode.Conflict,(await impostor.GetAsync("/api/agent/lifecycle")).StatusCode);
    }

    [DisposableDatabaseFact]
    public async Task Authentication_rechecks_fresh_row_after_waiting_for_concurrent_pause()
    {
        await using var f=await Fixture.Start();var a=await f.Create("0.9.0",true);
        await using var connection=await f.Source.OpenConnectionAsync();await using var tx=await connection.BeginTransactionAsync();
        await using(var command=new NpgsqlCommand("SELECT id FROM clients WHERE id=$1 FOR UPDATE",connection)){command.Parameters.AddWithValue(a.Id);await command.ExecuteScalarAsync();}
        var request=a.Http.GetAsync("/api/agent/config");
        var waiting=false;
        for(var i=0;i<100;i++) {
            var rows=await f.Db.Query("SELECT 1 FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE 'UPDATE clients SET agent_instance_id=%' AND datname=current_database()");
            if(rows.Count>0){waiting=true;break;}await Task.Delay(20);
        }
        Assert.True(waiting,"Config request must reach the blocked binding step before pause commits.");
        await using(var command=new NpgsqlCommand("UPDATE clients SET enabled=false WHERE id=$1",connection)){command.Parameters.AddWithValue(a.Id);await command.ExecuteNonQueryAsync();}
        await tx.CommitAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,(await request).StatusCode);
    }

    [DisposableDatabaseFact]
    public async Task Legacy_pending_decommission_drains_reports_without_any_credentials_or_new_work()
    {
        await using var f=await Fixture.Start();var a=await f.Create("0.8.0",true);var unsupported=await f.Create("0.8.0",false);
        var storage=new StorageService(f.Db,new EphemeralDataProtectionProvider());await storage.Save(new("fixture.invalid",21,"disposable","must-not-leak","/backup"));
        var ftp=new DeleteFixture{Fail=false};var deletion=new ClientDeletionService(f.Source,storage,ftp);
        await f.Db.Execute("UPDATE clients SET enabled=false WHERE id IN ($1,$2)",a.Id,unsupported.Id);
        // Paused alone does not grant a legacy drain session.
        Assert.Equal(HttpStatusCode.Unauthorized,(await a.Http.GetAsync("/api/agent/config")).StatusCode);
        await deletion.Delete(a.Id,new(a.Slug),default);await deletion.Delete(unsupported.Id,new(unsupported.Slug),default);
        await f.Db.Execute("UPDATE clients SET pending_results=1,agent_telemetry='{\"currentPhase\":\"ZipReady\",\"pendingResultCount\":1}'::jsonb WHERE id=$1",a.Id);
        var config=await a.Http.GetFromJsonAsync<JsonElement>("/api/agent/config");
        Assert.Equal(a.Id,config.GetProperty("clientId").GetGuid());Assert.Equal("fixture",config.GetProperty("database").GetString());Assert.Equal("/backup/"+a.Slug,config.GetProperty("remoteDirectory").GetString());
        Assert.Equal(JsonValueKind.Null,config.GetProperty("storage").ValueKind);Assert.Equal(JsonValueKind.Null,config.GetProperty("sql").ValueKind);
        var now=DateTimeOffset.UtcNow;var result=new RunInput(Guid.NewGuid(),null,"Full","Succeeded",now,now,1,"completed.zip","ok");
        Assert.Equal(HttpStatusCode.Created,(await a.Http.PostAsJsonAsync("/api/agent/results",result)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await a.Http.PostAsJsonAsync("/api/agent/results",result)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await unsupported.Http.PostAsJsonAsync("/api/agent/results",result)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await unsupported.Http.GetAsync("/api/agent/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await a.Http.GetAsync("/api/agent/commands/active")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await a.Http.PostAsJsonAsync("/api/agent/commands/claim",new{})).StatusCode);
        config=await a.Http.GetFromJsonAsync<JsonElement>("/api/agent/config");Assert.Equal(JsonValueKind.Null,config.GetProperty("storage").ValueKind);
        // A recovered Current can never turn advisory telemetry into credentials, deletion or an ACK.
        Assert.Equal(409,((IStatusCodeHttpResult)await deletion.Finalize(a.Id,default)).StatusCode);Assert.Equal(0,ftp.Calls);
        Assert.Null((await f.Db.Query("SELECT deactivation_confirmed_at FROM clients WHERE id=$1",a.Id))[0]["deactivation_confirmed_at"]);
        Assert.Single(await f.Db.Query("SELECT id FROM runs WHERE client_id=$1",a.Id));
        await f.Db.Execute("UPDATE clients SET agent_version='0.9.0' WHERE id=$1",a.Id);
        Assert.Equal(HttpStatusCode.Unauthorized,(await a.Http.GetAsync("/api/agent/config")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await a.Http.PostAsJsonAsync("/api/agent/results",result)).StatusCode);
    }

    sealed class DeleteFixture:IClientDirectoryDeletion {
        public bool Fail=true;public int Calls;
        public Task<DeletionCounts> Delete(Storage storage,string slug,CancellationToken ct){Calls++;if(Fail)throw new IOException("disposable failure");return Task.FromResult(new DeletionCounts(1,1));}
    }
    sealed record Client(Guid Id,string Slug,HttpClient Http);
    sealed class Fixture:IAsyncDisposable
    {
        public required NpgsqlDataSource Source;public required Database Db;public required HttpClient Admin;public required string Url;public required string Digest;public required byte[] Package;
        public required Process Process;public required NpgsqlDataSource Owner;public required string Schema;public required string DirectoryPath;
        public HttpClient Http(){var h=new HttpClient(new HttpClientHandler{UseCookies=true}){BaseAddress=new Uri(Url)};h.DefaultRequestHeaders.Add("X-Requested-With","BackupControl");return h;}
        public async Task<Client> Create(string version,bool capable) {
            var slug="fixture-"+Guid.NewGuid().ToString("N");var response=await Admin.PostAsJsonAsync("/api/clients",new{name=slug,slug,database="fixture",expectedIntervalHours=24});response.EnsureSuccessStatusCode();
            var c=await response.Content.ReadFromJsonAsync<JsonElement>();var id=c.GetProperty("id").GetGuid();var h=Http();h.DefaultRequestHeaders.Authorization=new("Bearer",c.GetProperty("agentToken").GetString());h.DefaultRequestHeaders.Add("X-Agent-Instance",Guid.NewGuid().ToString());
            (await h.PostAsJsonAsync("/api/agent/heartbeat",new{machineName="fixture",agentVersion=version,remoteUpdateSupported=capable})).EnsureSuccessStatusCode();return new(id,slug,h);
        }
        public static async Task<Fixture> Start() {
            var owner=NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("API_TEST_DATABASE")!);var schema="test_"+Guid.NewGuid().ToString("N");await using(var c=owner.CreateCommand("CREATE SCHEMA "+schema))await c.ExecuteNonQueryAsync();
            var cs=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("API_TEST_DATABASE")!){SearchPath=schema};var source=NpgsqlDataSource.Create(cs.ConnectionString);var db=new Database(source);await db.Initialize();
            var dir=Path.Combine(Path.GetTempPath(),schema);System.IO.Directory.CreateDirectory(Path.Combine(dir,"packages"));var package=new byte[]{1,2,3,4};var digest=Convert.ToHexString(SHA256.HashData(package)).ToLowerInvariant();
            await File.WriteAllBytesAsync(Path.Combine(dir,"backup-control-agent-win-x64.zip"),package);await File.WriteAllBytesAsync(Path.Combine(dir,"packages",digest+".zip"),package);await File.WriteAllTextAsync(Path.Combine(dir,"release.json"),JsonSerializer.Serialize(new{version="0.8.0",sha256=digest}));
            // The API and test process need the same persisted protection keys for installation settings.
            var keys=Path.Combine(dir,"keys");System.IO.Directory.CreateDirectory(keys);var provider=DataProtectionProvider.Create(new DirectoryInfo(keys),b=>b.SetApplicationName("BackupControl"));
            var installation=new InstallationService(source,provider,new ConfigurationBuilder().Build());await installation.Save(new("https://central.example",SqlPassword:"fixture"));
            var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();var url="http://127.0.0.1:"+port;
            var dll=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../api/bin/Debug/net10.0/BackupControl.dll"));
            var psi=new ProcessStartInfo("C:/Program Files/dotnet/dotnet.exe"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};psi.ArgumentList.Add(dll);
            psi.Environment["ConnectionStrings__Database"]=cs.ConnectionString;psi.Environment["ADMIN_PASSWORD"]="fixture-admin-password";psi.Environment["ASPNETCORE_URLS"]=url;psi.Environment["DATA_PROTECTION_KEYS_DIR"]=keys;psi.Environment["INSTALLER_DISTRIBUTION_DIR"]=dir;
            var process=Process.Start(psi)!;var log=new System.Text.StringBuilder();process.OutputDataReceived+=(_,e)=>{lock(log)log.AppendLine(e.Data);};process.ErrorDataReceived+=(_,e)=>{lock(log)log.AppendLine(e.Data);};process.BeginOutputReadLine();process.BeginErrorReadLine();
            var f=new Fixture{Source=source,Db=db,Admin=null!,Url=url,Digest=digest,Package=package,Process=process,Owner=owner,Schema=schema,DirectoryPath=dir};f.Admin=f.Http();
            for(var i=0;i<100;i++){if(process.HasExited)throw new Exception(log.ToString());try{if((await f.Admin.GetAsync("/api/health")).IsSuccessStatusCode)break;}catch(HttpRequestException){}await Task.Delay(100);}
            var login=await f.Admin.PostAsJsonAsync("/api/auth/login",new{username="admin",password="fixture-admin-password"});if(!login.IsSuccessStatusCode)throw new Exception(await login.Content.ReadAsStringAsync()+log);return f;
        }
        public async ValueTask DisposeAsync(){Admin.Dispose();if(!Process.HasExited){Process.Kill(true);await Process.WaitForExitAsync();}Process.Dispose();await Source.DisposeAsync();await using(var c=Owner.CreateCommand("DROP SCHEMA "+Schema+" CASCADE"))await c.ExecuteNonQueryAsync();await Owner.DisposeAsync();System.IO.Directory.Delete(DirectoryPath,true);}
    }
}
