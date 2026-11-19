using Xunit;
public class RegistrationPolicyTests
{
    [Fact] public void RejectsMissingProofAndControlCharacters()
    {
        var valid=new RegistrationInput(Guid.NewGuid(),Guid.NewGuid(),new string('a',64),"PC-CLIENTE","Cliente");
        RegistrationPolicy.Validate(valid);
        Assert.Throws<ArgumentException>(()=>RegistrationPolicy.Validate(valid with{DeviceSecret="123"}));
        Assert.Throws<ArgumentException>(()=>RegistrationPolicy.Validate(valid with{InstanceId=Guid.Empty}));
        Assert.Throws<ArgumentException>(()=>RegistrationPolicy.Validate(valid with{RequestedName="Cliente\nInjected"}));
    }
    [Fact] public void ApprovalClientUsesNormalScheduleAndSlugRules()
    {
        RegistrationPolicy.ValidateClient(new("Cliente","cliente01","ATXDADOS",24,60,true,4));
        Assert.Throws<ArgumentException>(()=>RegistrationPolicy.ValidateClient(new("Cliente","../escape","ATXDADOS")));
        Assert.Throws<ArgumentException>(()=>RegistrationPolicy.ValidateClient(new("Cliente","cliente01","ATXDADOS",4,60,true,4)));
    }
}
