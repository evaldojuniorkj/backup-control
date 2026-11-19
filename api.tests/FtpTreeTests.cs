using Xunit;
public class FtpTreeTests {
 [Theory] [InlineData("../olimpico")] [InlineData("/etc")] [InlineData("a/../b")] [InlineData("a\\b")] [InlineData("a//b")] [InlineData("a\r\nQUIT")] public void RejectsPathsOutsideRoot(string path)=>Assert.Throws<ArgumentException>(()=>FtpTreePolicy.Resolve("/backup",path));
 [Fact] public void AllowsRootAndActualNames(){Assert.Equal("/backup",FtpTreePolicy.Resolve("/backup/",null));Assert.Equal("/backup/Conveniência/2026",FtpTreePolicy.Resolve("/backup","Conveniência/2026"));Assert.False(FtpTreePolicy.SafeName(".."));Assert.False(FtpTreePolicy.SafeName("a/b"));}
}
