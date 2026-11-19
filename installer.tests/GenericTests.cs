using BackupControl.Setup;
using System.Net;
using System.Text;
using Xunit;
public class GenericTests
{
    [Fact] public async Task ResumingAfterTokenWasSavedPollsWithoutMutatingOriginalRegistration()
    {
        var registration=DeviceRegistration.Create("https://central.example.com","Cliente",Guid.NewGuid());var handler=new RecordingHandler(registration);using var http=new HttpClient(handler);
        await new GenericEnrollmentClient(registration.CentralUrl,http).Resume(registration,new string('b',64),default);
        Assert.Equal(HttpMethod.Get,handler.LastMethod);
    }
    [Fact] public void RetryReceivesCurrentSqlCredentialsWithoutChangingLocalFolders()
    {
        var initial=new SqlInstallConfig("localhost","old","old-secret",false,"service-account","C:\\backup");
        var resolved=InstallerEngine.ResolveSql(initial,new BackupControl.Agent.SqlRuntimeConfig("localhost\\SQL","new","new-secret",true,"revision"));
        Assert.Equal("new-secret",resolved.Password);Assert.Equal(initial.BackupDirectory,resolved.BackupDirectory);Assert.Equal(initial.ServiceIdentity,resolved.ServiceIdentity);
    }
    [Fact] public void UniversalTrailerContainsOnlyCentralOrigin()
    {
        using var file=Trailer("{\"centralUrl\":\"https://central.example.com\"}");
        Assert.Equal("https://central.example.com",GenericBootstrap.Read(file).CentralUrl);
        using var secret=Trailer("{\"centralUrl\":\"https://central.example.com\",\"enrollmentKey\":\"secret\"}");
        Assert.Throws<SetupException>(()=>GenericBootstrap.Read(secret));
    }
    [Fact] public void ApprovedIdentityCannotReplaceAnExistingInstance()
    {
        var expected=Guid.NewGuid();Assert.Equal(expected,InstallerEngine.ResolveInstance(null,null,expected));
        Assert.Throws<SetupException>(()=>InstallerEngine.ResolveInstance(Guid.NewGuid(),null,expected));
    }
    [Fact] public void RegistrationIdentityIsProtectedAndStableAcrossRestart()
    {
        var root=Path.Combine(Path.GetTempPath(),"bc-generic-"+Guid.NewGuid());
        try{var store=new GenericRegistrationStore(root);var registration=DeviceRegistration.Create("https://central.example.com","Cliente",Guid.NewGuid());store.Save(registration);Assert.Equal(registration,store.Load());Assert.DoesNotContain(registration.DeviceSecret,Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root,"registration.dat"))));}
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    [Fact] public async Task DeviceProofIsOnlyHeaderAndApprovalOriginMustMatch()
    {
        var registration=DeviceRegistration.Create("https://central.example.com","Cliente",Guid.NewGuid());
        var handler=new RecordingHandler(registration);using var http=new HttpClient(handler);var client=new GenericEnrollmentClient(registration.CentralUrl,http);
        var pending=await client.Poll(registration,default);Assert.Equal("Pending",pending.Status);Assert.False(handler.LastUri!.Query.Contains(registration.DeviceSecret));Assert.Equal(registration.DeviceSecret,handler.Proof);
        var approved=pending with{Status="Approved",ClientId=Guid.NewGuid(),ClientName="Cliente",CentralUrl="https://other.example.com",EnrollmentKey=new string('a',64)};
        Assert.Throws<SetupException>(()=>GenericEnrollmentClient.ApprovedBootstrap(registration,approved));
        Assert.Equal(approved.ClientId,GenericEnrollmentClient.ApprovedBootstrap(registration,approved with{CentralUrl=registration.CentralUrl}).ClientId);
    }
    private static MemoryStream Trailer(string json){var bytes=Encoding.UTF8.GetBytes(json);var file=new MemoryStream();file.Write(new byte[64]);file.Write(bytes);file.Write(BitConverter.GetBytes((long)bytes.Length));file.Write("BCGEN001"u8);file.Position=0;return file;}
    private sealed class RecordingHandler(DeviceRegistration registration):HttpMessageHandler
    {
        public Uri? LastUri;public string? Proof;public HttpMethod? LastMethod;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){LastUri=request.RequestUri;LastMethod=request.Method;Proof=request.Headers.GetValues("X-Registration-Secret").Single();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(System.Text.Json.JsonSerializer.Serialize(new{registrationId=registration.RegistrationId,status="Pending",code="ABCDE-FGHIJ",expiresAt=DateTimeOffset.UtcNow.AddMinutes(15),machineName=registration.MachineName,requestedName=registration.RequestedName}),Encoding.UTF8,"application/json")});}
    }
}
