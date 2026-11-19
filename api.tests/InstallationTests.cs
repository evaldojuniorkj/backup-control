using Xunit;
public class InstallationTests
{
 [Theory]
 [InlineData("http://localhost:5181")]
 [InlineData("http://127.0.0.1")]
 [InlineData("https://example.com/path")]
 public void SettingsRejectInsecureOrNonOriginUrls(string url)=>Assert.Throws<ArgumentException>(()=>InstallationService.Origin(url));
 [Fact] public void CertificateTrustDefaultsToFalse()=>Assert.False(new InstallationInput("https://example.com").TrustServerCertificate);
}
