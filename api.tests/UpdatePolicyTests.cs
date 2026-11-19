using Xunit;

public class UpdatePolicyTests
{
    [Theory]
    [InlineData("Pending","Waiting",true)]
    [InlineData("Pending","Applying",false)]
    [InlineData("Downloading","Applying",true)]
    [InlineData("Downloading","RolledBack",true)]
    [InlineData("Pending","RolledBack",false)]
    [InlineData("RolledBack","Applying",false)]
    [InlineData("Applying","Verifying",true)]
    [InlineData("Succeeded","Failed",false)]
    [InlineData("Verifying","RolledBack",true)]
    public void Transitions_are_explicit(string from,string to,bool allowed)=>Assert.Equal(allowed,UpdatePolicy.CanTransition(from,to));

    [Fact]
    public void Success_requires_fresh_matching_heartbeat()
    {
        var now=DateTimeOffset.UtcNow;
        Assert.False(UpdatePolicy.Confirmed("0.8.0","0.7.0",now,now.AddMinutes(-1),now));
        Assert.False(UpdatePolicy.Confirmed("0.8.0","0.8.0",now.AddMinutes(-8),now.AddMinutes(-10),now));
        Assert.False(UpdatePolicy.Confirmed("0.8.0","0.8.0",now.AddMinutes(-2),now.AddMinutes(-1),now));
        Assert.True(UpdatePolicy.Confirmed("0.8.0","0.8.0",now,now.AddMinutes(-1),now));
    }

    [Theory]
    [InlineData("0.7.0","0.8.0",true)]
    [InlineData("0.8.0","0.8.0",false)]
    [InlineData("0.9.0","0.8.0",false)]
    [InlineData("unknown","0.8.0",false)]
    public void Only_known_older_versions_are_eligible(string installed,string target,bool eligible)=>Assert.Equal(eligible,UpdatePolicy.IsUpgrade(installed,target));

    [Fact]
    public void Skip_cannot_claim_manual_command_or_file()
    {
        var input=new RunInput(Guid.NewGuid(),null,"Full","Skipped",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,0,"","Sem alterações no banco.");
        Assert.True(RunPolicy.Valid(input));
        Assert.False(RunPolicy.Valid(input with{CommandId=Guid.NewGuid()}));
        Assert.False(RunPolicy.Valid(input with{FileName="backup.zip"}));
        Assert.False(RunPolicy.Valid(input with{SizeBytes=1}));
        Assert.False(RunPolicy.Valid(input with{Message=""}));
    }
}
