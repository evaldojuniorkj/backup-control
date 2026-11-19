using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace BackupControl.Agent;

public sealed class CentralClient : ICentral, IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim telemetryLock=new(1,1);
    public CentralClient(LocalSettings settings,bool controlOnly=false)
    {
        Safety.Validate(controlOnly?settings with{SqlConnectionString="disabled"}:settings);
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(settings.CentralUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.AgentToken);
        http.DefaultRequestHeaders.Add("X-Agent-Instance", settings.InstanceId.ToString());
    }
    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new AgentUnauthorizedException();
        if ((int)response.StatusCode >= 500) throw new HttpRequestException("Central temporariamente indisponível.");
        if (!response.IsSuccessStatusCode) throw new CentralRejectedException((int)response.StatusCode);
    }
    public async Task<AgentDeactivation?> Lifecycle(CancellationToken ct)
    {
        using var response=await http.GetAsync("api/agent/lifecycle",ct);
        if(response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound)return null;
        Check(response);return await response.Content.ReadFromJsonAsync<AgentDeactivation>(ct)??throw new IOException("Controle remoto vazio.");
    }
    public async Task ConfirmDeactivation(Guid id,CancellationToken ct)
    {
        using var response=await http.PostAsJsonAsync("api/agent/lifecycle/"+id+"/confirm",new{},ct);Check(response);
    }
    public async Task<ClientConfig> Config(CancellationToken ct)
    {
        using var response = await http.GetAsync("api/agent/config", ct); Check(response);
        return await response.Content.ReadFromJsonAsync<ClientConfig>(ct) ?? throw new IOException("Configuração vazia.");
    }
    public async Task Heartbeat(CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/agent/heartbeat", new { machineName = Environment.MachineName, agentVersion = AgentVersion.Current,remoteUpdateSupported=AgentVersion.RemoteUpdateSupported,remoteDeactivationSupported=true }, ct); Check(response);
    }
    public async Task Status(AgentTelemetry status,CancellationToken ct)
    {
        await telemetryLock.WaitAsync(ct);
        try {using var response=await http.PostAsJsonAsync("api/agent/heartbeat",new{machineName=Environment.MachineName,agentVersion=AgentVersion.Current,remoteUpdateSupported=AgentVersion.RemoteUpdateSupported,remoteDeactivationSupported=true,telemetry=status},ct);Check(response);}
        finally {telemetryLock.Release();}
    }
    public async Task<RemoteCommand?> Claim(CancellationToken ct)
    {
        // A resposta do POST pode se perder após a retirada; primeiro recupere o comando já atribuído.
        using var active = await http.GetAsync("api/agent/commands/active", ct); Check(active);
        if (active.StatusCode != HttpStatusCode.NoContent) return await active.Content.ReadFromJsonAsync<RemoteCommand>(ct);
        using var response = await http.PostAsJsonAsync("api/agent/commands/claim", new { }, ct); Check(response);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.Content.ReadFromJsonAsync<RemoteCommand>(ct);
    }
    public async Task Report(BackupResult result, CancellationToken ct)
    { using var response = await http.PostAsJsonAsync("api/agent/results", result, ct); Check(response); }
    public void Dispose() => http.Dispose();
}


