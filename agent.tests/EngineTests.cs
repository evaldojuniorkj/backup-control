using BackupControl.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class EngineTests : IDisposable
{
    [Fact] public async Task RemoteDeactivationBlocksManualAndOfflineRestartsAndClearsSecretsBeforeAck()
    {
        store.State.Config=central.Settings;central.Deactivation=new(Guid.NewGuid(),true);
        central.OnConfirm=()=>{Assert.True(store.Cleared);Assert.Null(store.State.Config);Assert.NotNull(store.State.DeactivatedAt);};
        var engine=Engine();await engine.Cycle(true,"Full",default);
        Assert.True(store.State.DeactivationConfirmed);Assert.True(engine.PermanentlyDisabled);Assert.Equal(1,central.Confirmations);
        Assert.Equal(0,central.ConfigCalls);Assert.Equal(0,sql.Backups);Assert.Equal(0,upload.Uploads);
        central.Deactivation=null;central.Offline=true;await Engine().Cycle(true,"Differential",default);
        Assert.Equal(0,central.ConfigCalls);Assert.Equal(0,sql.Backups);
    }
    [Fact] public async Task LostAckRetriesFromDurableTombstoneWithoutRestoringCredentials()
    {
        central.Deactivation=new(Guid.NewGuid(),true);central.FailConfirm=true;
        await Engine().Cycle(true,"Full",default);Assert.False(store.State.DeactivationConfirmed);Assert.True(store.Cleared);
        central.FailConfirm=false;central.Deactivation=null;await Engine().Cycle(true,"Full",default);
        Assert.True(store.State.DeactivationConfirmed);Assert.Equal(2,central.Confirmations);Assert.Equal(0,central.ConfigCalls);Assert.Equal(0,sql.Backups);
    }
    [Fact] public async Task IncompleteRemoteUpdateDefersDeactivationAndNeverRunsBackups()
    {
        central.Deactivation=new(Guid.NewGuid(),false);await Engine().Cycle(true,"Full",default);
        Assert.True(store.State.Suspended);Assert.False(store.Cleared);Assert.Equal(0,central.Confirmations);Assert.Equal(0,central.ConfigCalls);Assert.Equal(0,sql.Backups);
    }
    [Fact] public async Task CredentialWipeFailureCannotBeAcknowledged()
    {
        central.Deactivation=new(Guid.NewGuid(),true);store.FailClear=true;
        await Assert.ThrowsAsync<IOException>(()=>Engine().Cycle(true,"Full",default));
        Assert.NotNull(store.State.DeactivationId);Assert.Equal(0,central.Confirmations);Assert.Equal(0,sql.Backups);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task LegacyReportOnlyConfigDrainsResultsWithoutSqlOrFtpEvenIfReportFails(bool failedReport)
    {
        store.State.Config=central.Settings;central.Settings=central.Settings with{Storage=null,Sql=null};central.FailReport=failedReport;
        store.State.Outbox.Add(new(Guid.NewGuid(),null,"Full","Succeeded",clock.Now,clock.Now,10,"old.zip","ok"));
        store.State.NextRunAt=clock.Now.AddDays(-1);
        await Engine().Cycle(true,"Full",default);
        Assert.Equal(0,sql.Backups);Assert.Equal(0,upload.Uploads);Assert.Equal(0,sql.Applications);
        Assert.Equal(failedReport?1:0,store.State.Outbox.Count);
    }
    [Fact] public async Task LegacyRecoveredOperationStaysPendingWithoutFtpCredentials()
    {
        store.State.Config=central.Settings;central.Settings=central.Settings with{Storage=null,Sql=null};
        store.State.Current=new Operation(Guid.NewGuid(),null,"Full",clock.Now,"Banco","/backup/cliente01",Path.Combine(directory,"x.bak"),Path.Combine(directory,"x.zip"),"x.zip","ZipReady",128,"abc");
        await Engine().Cycle(false,"Full",default);
        Assert.NotNull(store.State.Current);Assert.Equal(0,sql.Backups);Assert.Equal(0,upload.Uploads);Assert.Equal(0,central.Confirmations);
    }
    private DatabaseChangeStamp Stamp(string lsn="first")=>new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),lsn,"123");
    [Fact]
    public async Task ChangedDifferentialBaseWaitsForConfirmedScheduledFull()
    {
        central.Settings = central.Settings with { DifferentialIntervalHours = 4 };
        store.State.NextRunAt = clock.Now;
        await Engine().Cycle(false, "Full", default);
        var scheduledFull = store.State.NextRunAt!.Value;

        clock.Now = clock.Now.AddHours(4);
        sql.BaseLsn = "456";
        await Engine().Cycle(false, "Full", default);
        Assert.Equal("Failed", central.Results.Last().Status);
        Assert.Equal("Differential", central.Results.Last().BackupType);
        Assert.Equal(scheduledFull, central.Telemetry.Last().NextFullAt);
        Assert.Null(central.Telemetry.Last().NextDifferentialAt);

        clock.Now = clock.Now.AddMinutes(1);
        await Engine().Cycle(false, "Full", default);
        Assert.Equal(2, sql.Backups);
        Assert.Equal(1, upload.Uploads);

        clock.Now = scheduledFull;
        await Engine().Cycle(false, "Full", default);
        Assert.Equal("Full", central.Results.Last().BackupType);
        Assert.Equal("Succeeded", central.Results.Last().Status);
        Assert.Equal(scheduledFull.AddHours(4), central.Telemetry.Last().NextDifferentialAt);
    }

    [Fact]
    public async Task ManualFullDoesNotMoveScheduleOrEnableScheduledDifferential()
    {
        central.Settings = central.Settings with { DifferentialIntervalHours = 4 };
        var scheduledFull = clock.Now.AddHours(8);
        store.State.NextRunAt = scheduledFull;
        await Engine().Cycle(true, "Full", default);
        Assert.Equal(scheduledFull, store.State.NextRunAt);
        Assert.Null(central.Telemetry.Last().NextDifferentialAt);

        clock.Now = clock.Now.AddHours(4);
        await Engine().Cycle(false, "Full", default);
        Assert.Single(central.Results);
        await Engine().Cycle(true, "Differential", default);
        Assert.Equal("Succeeded", central.Results.Last().Status);
        Assert.Null(central.Telemetry.Last().NextDifferentialAt);

        sql.Stamp = Stamp();
        clock.Now = scheduledFull;
        await Engine().Cycle(false, "Full", default);
        Assert.Equal("Full", central.Results.Last().BackupType);
        Assert.Equal(scheduledFull.AddHours(4), central.Telemetry.Last().NextDifferentialAt);
    }

    [Fact]
    public async Task PendingDifferentialUploadDoesNotBlockDueScheduledFull()
    {
        central.Settings = central.Settings with { DifferentialIntervalHours = 4 };
        store.State.NextRunAt = clock.Now;
        await Engine().Cycle(false, "Full", default);

        clock.Now = store.State.NextRunAt!.Value.AddMinutes(-1);
        upload.Fail = true;
        await Engine().Cycle(false, "Full", default);
        Assert.Equal("Differential", central.Results.Last().BackupType);

        clock.Now = clock.Now.AddMinutes(1);
        upload.Fail = false;
        await Engine().Cycle(false, "Full", default);
        Assert.Equal("Full", central.Results.Last().BackupType);
        Assert.Equal("Succeeded", central.Results.Last().Status);
    }

    [Fact] public async Task UnchangedScheduledDifferentialSkipsWithoutMovingFullOrSuccessfulTimestamps()
    {
        sql.Stamp=Stamp();central.Settings=central.Settings with{DifferentialIntervalHours=4};
        await RunScheduledFull();var full=store.State.LastSuccessfulFullAt;var next=store.State.NextRunAt;
        clock.Now=clock.Now.AddHours(4);await Engine().Cycle(false,"Full",default);
        Assert.Equal("Skipped",central.Results.Last().Status);Assert.Equal("Differential",central.Results.Last().BackupType);
        Assert.Equal(1,sql.Backups);Assert.Equal(1,upload.Uploads);Assert.Equal(next,store.State.NextRunAt);Assert.Equal(full,store.State.LastSuccessfulFullAt);
        Assert.Empty(central.Results.Last().FileName);Assert.Equal(0,central.Results.Last().SizeBytes);
    }
    [Fact] public async Task UnchangedFullCanBeSkippedBeyondSevenDaysButManualStillExecutes()
    {
        sql.Stamp=Stamp();await Engine().Cycle(true,"Full",default);
        clock.Now=clock.Now.AddDays(8);await Engine().Cycle(false,"Full",default);
        Assert.Equal("Skipped",central.Results.Last().Status);Assert.Equal(1,sql.Backups);
        await Engine().Cycle(true,"Full",default);Assert.Equal(2,sql.Backups);Assert.Equal("Succeeded",central.Results.Last().Status);
        await Engine().Cycle(true,"Differential",default);Assert.Equal(3,sql.Backups);Assert.Equal("Differential",central.Results.Last().BackupType);Assert.Equal("Succeeded",central.Results.Last().Status);
    }
    [Fact] public async Task ChangedOrUnknownMarkerNeverSkipsAndFailedUploadNeverAdvancesProtectedMarker()
    {
        sql.Stamp=Stamp();central.Settings=central.Settings with{DifferentialIntervalHours=4};await RunScheduledFull();
        sql.Stamp=Stamp("changed");upload.Fail=true;clock.Now=clock.Now.AddHours(4);await Engine().Cycle(false,"Full",default);
        Assert.Equal("Failed",central.Results.Last().Status);Assert.Equal("first",store.State.SuccessfulChangeStamp!.LogEndLsn);
        upload.Fail=false;clock.Now=clock.Now.AddHours(4);await Engine().Cycle(false,"Full",default);Assert.Equal("Succeeded",central.Results.Last().Status);
        sql.Stamp=null;clock.Now=clock.Now.AddHours(4);await Engine().Cycle(false,"Full",default);Assert.Equal("Succeeded",central.Results.Last().Status);
        Assert.Equal(3,sql.Backups);
    }

    [Fact] public async Task ScheduledFtpFailureRetriesTheSameZipAcrossCyclesWithoutAnotherSqlBackup()
    {
        central.Settings=central.Settings with{DifferentialIntervalHours=4};
        upload.Fail=true;
        await RunScheduledFull();
        var failed=Assert.Single(central.Results);
        Assert.Equal("Failed",failed.Status);
        Assert.Null(central.Telemetry.Last().CurrentPhase);
        Assert.Equal(clock.Now.AddMinutes(15),central.Telemetry.Last().NextFullAt);

        clock.Now=clock.Now.AddMinutes(14);
        await Engine().Cycle(false,"Full",default);
        Assert.Equal(1,upload.Uploads);
        clock.Now=clock.Now.AddMinutes(1);
        await Engine().Cycle(false,"Full",default);
        Assert.Equal(2,upload.Uploads);
        Assert.Single(central.Results);
        Assert.Equal(1,sql.Backups);

        upload.Fail=false;
        clock.Now=clock.Now.AddMinutes(15);
        await Engine().Cycle(false,"Full",default);
        Assert.Equal(3,upload.Uploads);
        Assert.Equal(1,sql.Backups);
        var succeeded=central.Results.Last();
        Assert.Equal("Succeeded",succeeded.Status);
        Assert.Equal(failed.FileName,succeeded.FileName);
        Assert.Equal(clock.Now,succeeded.StartedAt);
        Assert.Contains($"{failed.StartedAt:O}",succeeded.Message);
        Assert.NotEqual(failed.EventId,succeeded.EventId);
        Assert.Equal(sql.BaseLsn,store.State.FullBaseLsn);
        Assert.Equal(failed.StartedAt,store.State.LastSuccessfulFullAt);
    }

    [Fact] public async Task OldPendingZipUsesTheRetryTimeForTheResultAndKeepsTheOriginalBackupTime()
    {
        central.Settings=central.Settings with{DifferentialIntervalHours=4};
        upload.Fail=true;
        await RunScheduledFull();
        var first=Assert.Single(central.Results);
        clock.Now=clock.Now.AddDays(31);
        upload.Fail=false;
        await Engine().Cycle(false,"Full",default);
        var recovered=central.Results.Last();
        Assert.Equal("Succeeded",recovered.Status);
        Assert.Equal(first.FileName,recovered.FileName);
        Assert.Equal(clock.Now,recovered.StartedAt);
        Assert.Equal(first.StartedAt,store.State.LastSuccessfulFullAt);
        Assert.Equal(1,sql.Backups);
    }

    [Fact] public async Task FailedScheduledFullKeepsNextDeadlineAndManualStillRunsImmediately()
    {
        central.Settings=central.Settings with{DifferentialIntervalHours=4};
        sql.FailVerification=true;
        await RunScheduledFull();
        var nextFull = store.State.NextRunAt;
        clock.Now=clock.Now.AddMinutes(15);
        await Engine().Cycle(false,"Full",default);
        Assert.Equal(nextFull,central.Telemetry.Last().NextFullAt);

        clock.Now=clock.Now.AddMinutes(15);
        await Engine().Cycle(false,"Full",default);
        Assert.Equal(1,sql.Backups);
        sql.FailVerification=false;
        await Engine().Cycle(true,"Full",default);
        Assert.Equal(2,sql.Backups);
        Assert.Equal(nextFull,store.State.NextRunAt);
        Assert.Equal("Succeeded",central.Results.Last().Status);
    }

    [Fact] public async Task ManualFullCanSupersedeScheduledFtpRetryWithoutSendingItsOldZip()
    {
        central.Settings=central.Settings with{DifferentialIntervalHours=4};
        upload.Fail=true;
        await RunScheduledFull();
        var failed=Assert.Single(central.Results);
        clock.Now=clock.Now.AddMinutes(1);
        upload.Fail=false;
        await Engine().Cycle(true,"Full",default);
        Assert.NotEqual(failed.FileName,central.Results.Last().FileName);
        Assert.Equal("Succeeded",central.Results.Last().Status);

        clock.Now=clock.Now.AddMinutes(14);
        await Engine().Cycle(false,"Full",default);
        Assert.Equal(2,upload.Uploads);
        Assert.Equal(2,sql.Backups);
    }

    [Fact] public async Task ManualDifferentialRejectedWithoutBaseLeavesThePendingFullAvailableForRetry()
    {
        central.Settings=central.Settings with{DifferentialIntervalHours=4};
        upload.Fail=true;
        await RunScheduledFull();
        var pending=Assert.Single(central.Results);
        await Engine().Cycle(true,"Differential",default);
        Assert.Equal("Failed",central.Results.Last().Status);

        clock.Now=clock.Now.AddMinutes(15);
        upload.Fail=false;
        await Engine().Cycle(false,"Full",default);
        Assert.Equal("Succeeded",central.Results.Last().Status);
        Assert.Equal(pending.FileName,central.Results.Last().FileName);
        Assert.Equal(1,sql.Backups);
    }

    [Fact] public async Task ChangingFtpDestinationDoesNotSendAPendingZipToTheNewHost()
    {
        central.Settings=central.Settings with{DifferentialIntervalHours=4};
        upload.Fail=true;
        await RunScheduledFull();
        var pending=Assert.Single(central.Results);
        central.Settings=central.Settings with{Storage=central.Settings.Storage! with{Host="another.example.com"}};
        upload.Fail=false;
        await Engine().Cycle(false,"Full",default);
        Assert.Single(central.Results);
        Assert.Null(store.State.PendingScheduledUpload);
        clock.Now=store.State.NextRunAt!.Value;
        await Engine().Cycle(false,"Full",default);

        Assert.Equal("Succeeded",central.Results.Last().Status);
        Assert.NotEqual(pending.FileName,central.Results.Last().FileName);
        Assert.Equal(2,sql.Backups);
        Assert.Null(store.State.PendingScheduledUpload);
    }

    [Fact] public async Task MissingPendingZipStopsTheUploadRetryWithoutContactingFtpOrRecreatingSqlBackup()
    {
        central.Settings=central.Settings with{DifferentialIntervalHours=4};
        upload.Fail=true;
        await RunScheduledFull();
        clock.Now=clock.Now.AddMinutes(15);
        await Engine(new FtpUploader()).Cycle(false,"Full",default);

        Assert.Null(store.State.PendingScheduledUpload);
        Assert.Equal(1,sql.Backups);
        Assert.Equal("Failed",central.Results.Last().Status);
        Assert.Contains("ZIP local ausente",central.Results.Last().Message);
    }
    [Fact] public async Task MissingBaseFileOrDifferentDatabaseIdentityNeverAllowsSkip()
    {
        sql.Stamp=Stamp();await Engine().Cycle(true,"Full",default);
        upload.FilePresent=false;clock.Now=clock.Now.AddDays(1);await Engine().Cycle(false,"Full",default);Assert.Equal("Succeeded",central.Results.Last().Status);
        upload.FilePresent=true;sql.Stamp=Stamp() with{RecoveryForkGuid=Guid.NewGuid()};clock.Now=clock.Now.AddDays(1);await Engine().Cycle(false,"Full",default);
        Assert.Equal(3,sql.Backups);
    }
    [Fact] public async Task ReturningToLastGoodRevisionClearsSqlFailure()
    {
        central.Settings=central.Settings with{Sql=new("localhost","sa","secret",true,"good")};await Engine().Cycle(false,"Full",default);
        central.Settings=central.Settings with{Sql=central.Settings.Sql! with{Revision="bad"}};sql.FailApply=true;await Engine().Cycle(false,"Full",default);
        central.Settings=central.Settings with{Sql=central.Settings.Sql! with{Revision="good"}};await Engine().Cycle(false,"Full",default);
        Assert.Equal("Applied",central.Telemetry.Last().SqlConfigStatus);Assert.Null(central.Telemetry.Last().SqlConfigError);Assert.Equal(2,sql.Applications);
    }
    [Fact] public async Task RemoteSqlRevisionAppliesOnceBetweenOperations()
    {
        central.Settings=central.Settings with{Sql=new("localhost","sa","secret",true,"revision-one")};
        await Engine().Cycle(false,"Full",default);await Engine().Cycle(false,"Full",default);
        Assert.Equal(1,sql.Applications);Assert.Equal("revision-one",central.Telemetry.Last().AppliedSqlRevision);
        central.Settings=central.Settings with{Sql=central.Settings.Sql! with{Revision="revision-two"}};
        await Engine().Cycle(false,"Full",default);Assert.Equal(2,sql.Applications);Assert.Equal(0,sql.Backups);
    }
    [Fact] public async Task FailedRemoteSqlChangeKeepsOutboxDeliveryButBlocksNewBackup()
    {
        central.FailReport=true;await Engine().Cycle(true,"Full",default);central.FailReport=false;
        central.Settings=central.Settings with{Sql=new("localhost","sa","never-expose",true,"bad-revision")};sql.FailApply=true;
        await Engine().Cycle(true,"Full",default);
        Assert.Empty(store.State.Outbox);Assert.Equal(1,sql.Backups);Assert.Equal("Failed",central.Telemetry.Last().SqlConfigStatus);
        Assert.DoesNotContain("never-expose",central.Telemetry.Last().SqlConfigError??"");
    }
    [Fact] public async Task ActiveOperationDefersRemoteSqlChange()
    {
        store.State.Config=central.Settings;store.State.Current=new Operation(Guid.NewGuid(),null,"Full",clock.Now,"Banco","/backup/cliente01",Path.Combine(directory,"x.bak"),Path.Combine(directory,"x.zip"),"x.zip","ZipReady",128,"abc");
        central.Settings=central.Settings with{Sql=new("localhost","sa","secret",true,"next-revision")};
        await Engine().Cycle(false,"Full",default);Assert.Equal(0,sql.Applications);Assert.Single(central.Results);
        await Engine().Cycle(false,"Full",default);Assert.Equal(1,sql.Applications);
    }
    [Fact] public async Task OutboxFailureIsExposedWithoutLosingResult()
    {
        central.FailReport=true;await Engine().Cycle(true,"Full",default);
        Assert.Equal(1,central.Telemetry.Last().PendingResultCount);Assert.NotNull(central.Telemetry.Last().LastResultError);Assert.NotNull(central.Telemetry.Last().AgentTime);
        central.FailReport=false;await Engine().Cycle(false,"Full",default);
        Assert.Equal(0,central.Telemetry.Last().PendingResultCount);Assert.Null(central.Telemetry.Last().LastResultError);Assert.Equal(1,sql.Backups);
    }
    private readonly string directory = Path.Combine(Path.GetTempPath(), "bc-engine-" + Guid.NewGuid());
    private readonly MemoryStore store = new();
    private readonly SqlStub sql = new();
    private readonly UploaderStub upload = new();
    private readonly CentralStub central = new();
    private readonly Clock clock=new();
    private AgentEngine Engine(IUploader? uploader=null) => new(new LocalSettings("http://localhost:5180", new string('a', 64), "Server=localhost;Integrated Security=True", directory), store, central, sql, new ArchiveStub(), uploader??upload, NullLogger<AgentEngine>.Instance,clock);
    private Task RunScheduledFull()
    {
        store.State.NextRunAt = clock.Now;
        return Engine().Cycle(false, "Full", default);
    }
    [Fact]
    public async Task TelemetryMatchesPersistedFullAndDifferentialDeadlines()
    {
        central.Settings=central.Settings with{DifferentialIntervalHours=4};
        await RunScheduledFull();
        var status=central.Telemetry.Last();
        Assert.Equal(store.State.NextRunAt,status.NextFullAt);
        Assert.Equal(store.State.NextDifferentialAt,status.NextDifferentialAt);
        Assert.True(status.HasFullBase);
        Assert.Equal(4,status.AppliedDifferentialIntervalHours);
        Assert.Null(status.CurrentBackupType);
        Assert.Contains(central.Telemetry,t=>t.CurrentPhase=="SqlStarted");
        Assert.Contains(central.Telemetry,t=>t.CurrentPhase=="SqlStarted"&&t.CurrentOrigin=="Scheduled");
    }
    [Fact]
    public async Task InitialScheduleIsReportedWithoutInventingSuccessfulBackup()
    {
        await Engine().Cycle(false,"Full",default);
        var status=central.Telemetry.Last();
        Assert.Equal(clock.Now.AddHours(24),status.NextFullAt);
        Assert.False(status.HasFullBase);
        Assert.Null(status.LastSuccessfulFullAt);
    }
    [Fact]
    public async Task ReportingFailureNeverReexecutesSql()
    {
        central.FailReport = true;
        await Engine().Cycle(true, "Full", default);
        Assert.Single(store.State.Outbox); Assert.Equal(1, sql.Backups);
        central.FailReport = false;
        await Engine().Cycle(false, "Full", default);
        Assert.Empty(store.State.Outbox); Assert.Equal(1, sql.Backups); Assert.Single(central.Results);
    }
    [Fact]
    public async Task InterruptedSqlIsReportedAsFailureWithoutRetry()
    {
        store.State.Config = central.Settings;
        store.State.Current = new Operation(Guid.NewGuid(), null, "Full", DateTimeOffset.UtcNow, "Banco", "/backup/cliente01", Path.Combine(directory, "test.bak"), Path.Combine(directory, "test.zip"), "test.zip", "SqlStarted");
        await Engine().Cycle(false, "Full", default);
        Assert.Equal(0, sql.Backups); Assert.Equal(0, upload.Uploads);
        Assert.Equal("Failed", Assert.Single(central.Results).Status);
    }
    [Fact]
    public async Task PackagedBackupResumesOnlyUpload()
    {
        store.State.Config = central.Settings;
        store.State.Current = new Operation(Guid.NewGuid(), null, "Full", DateTimeOffset.UtcNow, "Banco", "/backup/cliente01", Path.Combine(directory, "test.bak"), Path.Combine(directory, "test.zip"), "test.zip", "ZipReady", 128, "abc");
        await Engine().Cycle(false, "Full", default);
        Assert.Equal(0, sql.Backups); Assert.Equal(1, upload.Uploads);
        Assert.Equal("Succeeded", Assert.Single(central.Results).Status);
    }
    [Fact]
    public async Task VerificationFailurePreventsUpload()
    {
        sql.FailVerification = true;
        await Engine().Cycle(true, "Full", default);
        Assert.Equal(0, upload.Uploads); Assert.Equal("Failed", Assert.Single(central.Results).Status);
    }
    [Fact]
    public async Task RevokedTokenSuspendsOfflineScheduling()
    {
        store.State.Config = central.Settings; store.State.NextRunAt = DateTimeOffset.UtcNow.AddDays(-1); central.Revoked = true;
        await Engine().Cycle(false, "Full", default);
        Assert.True(store.State.Suspended); Assert.Equal(0, sql.Backups);
    }
    [Fact]
    public async Task NetworkOutageStillRunsCachedSchedule()
    {
        store.State.Config = central.Settings; store.State.NextRunAt = DateTimeOffset.UtcNow.AddDays(-1); central.Offline = true;
        await Engine().Cycle(false, "Full", default);
        Assert.Equal(1, sql.Backups); Assert.Single(store.State.Outbox);
    }
    [Fact]
    public async Task DifferentialDoesNotPostponeScheduledFull()
    {
        var expected = DateTimeOffset.UtcNow.AddHours(2); store.State.NextRunAt = expected;
        store.State.FullBaseLsn="123";store.State.FullBaseDatabase="Banco";store.State.LastSuccessfulFullAt=clock.Now;store.State.FullBaseDestination=Safety.DestinationIdentity(central.Settings);
        await Engine().Cycle(true, "Differential", default);
        Assert.Equal(expected, store.State.NextRunAt);
    }
    [Fact]
    public async Task ChangedIntervalUpdatesExistingSchedule()
    {
        var next = DateTimeOffset.UtcNow.AddHours(24); store.State.Config = central.Settings; store.State.NextRunAt = next;
        central.Settings = central.Settings with { ExpectedIntervalHours = 1 };
        await Engine().Cycle(false, "Full", default);
        Assert.Equal(next.AddHours(-23), store.State.NextRunAt);
    }
    [Fact]
    public async Task DifferentialRunsAfterFourHoursAndKeepsFullDeadline()
    {
        central.Settings=central.Settings with{DifferentialIntervalHours=4};
        await RunScheduledFull();
        var fullDeadline=store.State.NextRunAt;
        clock.Now=clock.Now.AddHours(3);await Engine().Cycle(false,"Full",default);
        Assert.Single(central.Results);
        clock.Now=clock.Now.AddHours(1);await Engine().Cycle(false,"Full",default);
        Assert.Equal("Differential",central.Results.Last().BackupType);
        Assert.Equal(fullDeadline,store.State.NextRunAt);
        Assert.Equal(clock.Now.AddHours(4),store.State.NextDifferentialAt);
    }
    [Fact]
    public async Task FullTakesPriorityWhenBothSchedulesAreDue()
    {
        central.Settings=central.Settings with{DifferentialIntervalHours=4};
        await RunScheduledFull();
        clock.Now=clock.Now.AddHours(24);await Engine().Cycle(false,"Full",default);
        Assert.Equal("Full",central.Results.Last().BackupType);
    }
    [Fact]
    public async Task EnablingDifferentialWaitsForFirstScheduledFull()
    {
        central.Settings=central.Settings with{DifferentialIntervalHours=4};
        await Engine().Cycle(false,"Full",default);
        Assert.Empty(central.Results);
        Assert.Null(central.Telemetry.Last().NextDifferentialAt);
        clock.Now=store.State.NextRunAt!.Value;
        await Engine().Cycle(false,"Full",default);
        Assert.Equal("Full",Assert.Single(central.Results).BackupType);
        Assert.Contains(central.Telemetry,t=>t.CurrentPhase=="SqlStarted"&&t.CurrentOrigin=="Scheduled");
    }
    [Fact]
    public async Task FailedFullNeverEnablesDifferentialsAndDoesNotLoopImmediately()
    {
        central.Settings=central.Settings with{DifferentialIntervalHours=4};sql.FailVerification=true;
        await RunScheduledFull();await Engine().Cycle(false,"Full",default);
        Assert.Equal(1,sql.Backups);Assert.Null(store.State.FullBaseLsn);Assert.Null(store.State.NextDifferentialAt);
        Assert.Equal(clock.Now.AddHours(24),central.Telemetry.Last().NextFullAt);
    }
    [Fact]
    public async Task UnexpectedDifferentialBaseIsNotUploaded()
    {
        await Engine().Cycle(true,"Full",default);sql.BaseLsn="999";
        await Engine().Cycle(true,"Differential",default);
        Assert.Equal("Failed",central.Results.Last().Status);Assert.Equal(1,upload.Uploads);Assert.Null(store.State.FullBaseLsn);
    }
    [Fact]
    public async Task ChangingFtpDestinationRequiresAnotherFull()
    {
        central.Settings=central.Settings with{DifferentialIntervalHours=4};
        await Engine().Cycle(true,"Full",default);
        central.Settings=central.Settings with{Storage=central.Settings.Storage! with{Host="another.example.com"}};
        await Engine().Cycle(false,"Full",default);
        Assert.Single(central.Results);
        clock.Now=store.State.NextRunAt!.Value;
        await Engine().Cycle(false,"Full",default);
        Assert.Equal(2,central.Results.Count);Assert.Equal("Full",central.Results.Last().BackupType);
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private sealed class MemoryStore : IStateStore
    {
        public bool Cleared,FailClear;
        public Task ClearBackupCredentials(bool clearToken,CancellationToken ct){if(FailClear)throw new IOException("wipe failed");Cleared=true;return Task.CompletedTask;}
        public AgentState State = new();
        public Task<AgentState> Read(CancellationToken ct = default) => Task.FromResult(State);
        public Task Write(AgentState s, CancellationToken ct = default) { State = s; return Task.CompletedTask; }
    }
    private sealed class SqlStub : ISqlBackup
    {
        public int Applications; public bool FailApply;
        public Task ApplyConfiguration(SqlRuntimeConfig config,string database,CancellationToken ct){Applications++;return FailApply?Task.FromException(new IOException("never-expose")):Task.CompletedTask;}
        public int Backups; public bool FailVerification;
        public string BaseLsn="123";
        public DatabaseChangeStamp? Stamp;
        public Task<DatabaseChangeStamp?> ReadChangeStamp(string database,CancellationToken ct)=>Task.FromResult(Stamp);
        public Task<string?> ReadBaseLsn(Operation op,CancellationToken ct)=>Task.FromResult<string?>(BaseLsn);
        public Task Check(string db, CancellationToken ct) => Task.CompletedTask;
        public Task Backup(Operation op, CancellationToken ct) { Backups++; return Task.CompletedTask; }
        public Task Verify(Operation op, CancellationToken ct) => FailVerification ? Task.FromException(new IOException("Invalid backup")) : Task.CompletedTask;
    }
    private sealed class ArchiveStub : IArchive { public Task<(long, string)> Create(Operation op, CancellationToken ct) => Task.FromResult((128L, "abc")); }
    private sealed class UploaderStub : IUploader
    {
        public int Uploads;public bool Fail;public bool? FilePresent=true;
        public Task<bool?> Exists(string directory,string fileName,long size,StorageConfig storage,CancellationToken ct)=>Task.FromResult(FilePresent);
        public Task Upload(Operation op, StorageConfig config, CancellationToken ct) { Uploads++; return Fail?Task.FromException(new IOException("FTP unavailable")):Task.CompletedTask; }
        public Task Check(string dir, StorageConfig config, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class CentralStub : ICentral
    {
        public ClientConfig Settings = new(Guid.NewGuid(), "cliente01", "Banco", 24, "/backup/cliente01", new("example.com", 21, "ftp", "password", "FTP", "EPSV"));
        public AgentDeactivation? Deactivation;public int ConfigCalls,Confirmations;public bool FailConfirm;public Action? OnConfirm;
        public Task<AgentDeactivation?> Lifecycle(CancellationToken ct)=>Task.FromResult(Deactivation);
        public Task ConfirmDeactivation(Guid id,CancellationToken ct){Confirmations++;OnConfirm?.Invoke();return FailConfirm?Task.FromException(new HttpRequestException()):Task.CompletedTask;}
        public bool FailReport, Revoked, Offline;
        public List<BackupResult> Results = [];
        public List<AgentTelemetry> Telemetry = [];
        public Task Status(AgentTelemetry status,CancellationToken ct) { Telemetry.Add(status); return Task.CompletedTask; }
        public Task<ClientConfig> Config(CancellationToken ct) {ConfigCalls++;return Revoked ? Task.FromException<ClientConfig>(new AgentUnauthorizedException()) : Offline ? Task.FromException<ClientConfig>(new HttpRequestException()) : Task.FromResult(Settings);}
        public Task Heartbeat(CancellationToken ct) => Task.CompletedTask;
        public Task<RemoteCommand?> Claim(CancellationToken ct) => Task.FromResult<RemoteCommand?>(null);
        public Task Report(BackupResult result, CancellationToken ct) { if (FailReport) throw new HttpRequestException(); Results.Add(result); return Task.CompletedTask; }
    }
    private sealed class Clock:TimeProvider
    {
        public DateTimeOffset Now=DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()=>Now;
    }
}
