using Xunit;
public class NetworkHealthTests {
 [Fact] public void ChangedAddressOnlyAlertsWhenConfiguredDestinationNoLongerMatches(){var now=DateTimeOffset.UtcNow;var s=new NetworkSnapshot(now,["2804::2"],["2804::1"],now,["2804::2"]);Assert.Contains(NetworkHealth.Evaluate(s,"2804::1",now),x=>x.Code=="ftp-ipv6-changed");Assert.Empty(NetworkHealth.Evaluate(s,"2804::2",now));}
 [Fact] public void StaleObservationDoesNotClaimAnAddressChange(){var now=DateTimeOffset.UtcNow;var s=new NetworkSnapshot(now.AddMinutes(-10),["2804::2"],["2804::1"],now,["2804::2"]);Assert.Single(NetworkHealth.Evaluate(s,"2804::1",now));Assert.Equal("network-monitor-stale",NetworkHealth.Evaluate(s,"2804::1",now)[0].Code);}
 [Fact] public void ExternalFtpAddressDoesNotBecomeALocalMismatch(){var now=DateTimeOffset.UtcNow;var s=new NetworkSnapshot(now,["2804::2"],[],null,["2804::2"]);Assert.Empty(NetworkHealth.Evaluate(s,"2804::9",now));}
 [Fact] public void DnsMismatchHasSeparateCause(){var now=DateTimeOffset.UtcNow;var s=new NetworkSnapshot(now,["2804::2"],[],null,["2804::1"]);Assert.Contains(NetworkHealth.Evaluate(s,"ftp.example.com",now),x=>x.Code=="central-ipv6-dns");}
}
