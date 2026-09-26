using MediaCli.Transcode.Support;
using Xunit;

namespace MediaCli.Transcode.Tests;

public class BuildInfoTests
{
    [Fact]
    public void AppVersion_MatchesSemanticVersionFormat()
    {
        var ver = BuildInfo.AppVersion;
        Assert.False(string.IsNullOrWhiteSpace(ver));
        var parts = ver.Split('.');
        Assert.True(parts.Length >= 3, $"AppVersion '{ver}' should have at least 3 parts (Major.Minor.Patch)");
        Assert.Equal("1", parts[0]);
        Assert.Equal("0", parts[1]);
    }

    [Fact]
    public void Metadata_InjectedCorrectly()
    {
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.InformationalVersion));
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.GitCommit));
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.BuildTime));
        Assert.False(string.IsNullOrWhiteSpace(BuildInfo.DaysSinceEpoch));
        Assert.True(int.TryParse(BuildInfo.DaysSinceEpoch, out var days) && days > 0);
    }

    [Fact]
    public void DisplayString_ContainsVersionAndMetadata()
    {
        var display = BuildInfo.DisplayString;
        Assert.StartsWith("v1.0.", display);
        Assert.Contains(BuildInfo.GitCommit, display);
    }
}
