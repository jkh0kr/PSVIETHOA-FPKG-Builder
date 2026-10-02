using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>Phiên bản kế tiếp của bản vá: tăng nhóm CUỐI (hotfix), rollover qua nhóm giữa khi 999.</summary>
public class NextVersionPolicyTests
{
    [Theory]
    [InlineData("01.024.000", "01.024.001")]
    [InlineData("01.024.001", "01.024.002")]
    [InlineData("01.024.999", "01.025.000")]
    [InlineData("01.999.999", "02.000.000")]
    [InlineData("99.999.999", null)]
    [InlineData("rác", null)]
    public void NextVersion_BumpsLastSegment(string input, string? expected) =>
        Assert.Equal(expected, SonySdkPatchReference.NextVersion(input));

    [Fact]
    public void CompareVersions_LastSegmentHigher()
    {
        Assert.True(SonySdkPatchReference.CompareVersions("01.024.001", "01.024.000") > 0);
        Assert.True(SonySdkPatchReference.CompareVersions("01.024.000", "01.024.001") < 0);
        Assert.True(SonySdkPatchReference.CompareVersions("01.024.001", "01.024.001") == 0);
    }
}
