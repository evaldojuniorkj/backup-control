using Xunit;
public class CommunicationTests
{
    readonly DateTimeOffset Now=new(2026,11,16,20,20,0,TimeSpan.Zero);
    [Fact] public void ClockAdjustedRecoveryPreservesDurationAndRawIsSeparate(){var result=ResultTimePolicy.Resolve(Now.AddMinutes(50),Now.AddMinutes(55),Now,3600,Now);Assert.Equal("ClockAdjusted",result.Quality);Assert.Equal(Now.AddMinutes(-5),result.FinishedAt);Assert.Equal(TimeSpan.FromMinutes(5),result.FinishedAt-result.StartedAt);}
    [Fact] public void UnknownFutureClockUsesReceiptWithoutInventingTrustedTime(){var result=ResultTimePolicy.Resolve(Now.AddMinutes(50),Now.AddMinutes(55),Now,null,null);Assert.Equal("ReceiptEstimated",result.Quality);Assert.Equal(Now,result.FinishedAt);}
    [Fact] public void StaleClockCannotNormalizeFutureReport(){var result=ResultTimePolicy.Resolve(Now.AddMinutes(50),Now.AddMinutes(55),Now,3600,Now.AddHours(-2));Assert.Equal("ReceiptEstimated",result.Quality);}
    [Fact] public void HistoricalResultWithNormalClockRemainsExact(){var result=ResultTimePolicy.Resolve(Now.AddDays(-2),Now.AddDays(-2).AddMinutes(5),Now,2,Now);Assert.Equal("AgentClock",result.Quality);Assert.Equal(Now.AddDays(-2),result.StartedAt);}
    [Fact] public void ExtremeReportedDateDoesNotOverflowAdjustment(){var result=ResultTimePolicy.Resolve(DateTimeOffset.MinValue.AddMinutes(1),DateTimeOffset.MinValue.AddMinutes(2),Now,3600,Now);Assert.Equal("AgentClock",result.Quality);}
    [Fact] public void LegacyMidBackupConfigurationTimeCannotBecomeClockSample(){var t=new AgentTelemetry(null,null,null,false,true,24,4,"ATXDADOS",Now.AddHours(-1),"Full","SqlStarted",Now.AddHours(-1));Assert.Null(ClockSamplePolicy.Select(t));Assert.Equal(Now,ClockSamplePolicy.Select(t with{AgentTime=Now}));Assert.Equal(t.ConfigAppliedAt,ClockSamplePolicy.Select(t with{CurrentBackupType=null,CurrentPhase=null}));}
    [Theory][InlineData("ATXDADOS-20261116120000-(d1e4b9bd-59ea-42a3-8078-faeef65dda9e)-Diff.zip","Differential")][InlineData("ATXDADOS-20261115160002-(d1e4b9bd-59ea-42a3-8078-faeef65dda9e)-Full.zip","Full")]
    public void LegacyTypeDoesNotGuessTimezone(string file,string type){var metadata=BackupFileMetadata.Parse(file);Assert.Equal(type,metadata.Type);Assert.Null(metadata.CreatedAt);}
}
