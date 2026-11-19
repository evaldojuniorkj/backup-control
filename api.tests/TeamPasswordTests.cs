using Xunit;
public class TeamPasswordTests {
 [Fact] public void SaltedHashesVerifyOnlyExactPassword(){var a=TeamPasswords.Hash("UmaSenhaMuitoForte-123");var b=TeamPasswords.Hash("UmaSenhaMuitoForte-123");Assert.NotEqual(a,b);Assert.True(TeamPasswords.Verify("UmaSenhaMuitoForte-123",a));Assert.False(TeamPasswords.Verify("OutraSenhaMuitoForte",a));Assert.False(TeamPasswords.Verify("x","invalid"));}
 [Fact] public void AcceptsEightCharactersAndRejectsSeven(){var encoded=TeamPasswords.Hash("Abcd1234");Assert.True(TeamPasswords.Verify("Abcd1234",encoded));Assert.Throws<ArgumentException>(()=>TeamPasswords.Validate("Abc1234"));}
 [Theory] [InlineData("short")] [InlineData("")] public void RejectsWeakPassword(string value)=>Assert.Throws<ArgumentException>(()=>TeamPasswords.Validate(value));
}
