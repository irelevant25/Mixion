using Mixion.Host.Diagnostics;
using Xunit;

namespace Mixion.Host.Tests;

/// <summary>
/// <see cref="AppVersion"/> reports the version <c>build.ps1</c> stamped — as
/// released (<c>1.2.0</c>), not as the four-part assembly version (<c>1.2.0.0</c>).
/// </summary>
public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.0+a4994b2479628acac2bf639c26a97d2ea54c13ac", "1.2.0")]
    [InlineData("1.3.0-beta.1+a4994b2", "1.3.0-beta.1")]
    [InlineData("1.2.0-3-gabc1234-dirty+abc1234", "1.2.0-3-gabc1234-dirty")]
    [InlineData("1.2.0", "1.2.0")]
    public void Resolve_IsTheStampedVersionWithoutBuildMetadata(string informational, string expected)
    {
        Assert.Equal(expected, AppVersion.Resolve(informational, new Version(9, 9, 9, 9)));
    }

    [Fact]
    public void Resolve_FallsBackToThreePartsOfTheAssemblyVersion()
    {
        Assert.Equal("1.2.0", AppVersion.Resolve(null, new Version(1, 2, 0, 0)));
        Assert.Equal("1.2.0", AppVersion.Resolve("  ", new Version(1, 2)));
    }

    [Fact]
    public void Resolve_WithNothingToGoOn_IsZero()
    {
        Assert.Equal("0.0.0", AppVersion.Resolve(null, null));
    }

    [Fact]
    public void Current_IsNotAFourPartAssemblyVersion()
    {
        Assert.DoesNotMatch(@"^\d+\.\d+\.\d+\.\d+$", AppVersion.Current);
    }
}
