using Jellyfin.Plugin.C411Torbox.Configuration;
using Jellyfin.Plugin.C411Torbox.Models;
using Jellyfin.Plugin.C411Torbox.Services;

namespace Jellyfin.Plugin.C411Torbox.Tests;

public class ApprovalRulesTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static readonly PluginConfiguration Config = new()
    {
        AutoApproveWhenCached = true,
        MovieMaxSizeGb = 15,
        EpisodeMaxSizeGb = 5,
        SeasonMaxSizeGb = 30,
        MinSeedersForAutoApproval = 5,
    };

    [Theory]
    [InlineData(RequestScope.Movie, 100, 0)]
    [InlineData(RequestScope.Episode, 100, 0)]
    [InlineData(RequestScope.Season, 500, 0)]
    public void Cached_IsAutoApproved_WhateverSizeAndSeeders(RequestScope scope, long sizeGb, int seeders)
        => Assert.Equal(AutoApprovalReason.Cached, ApprovalRules.Evaluate(scope, sizeGb * Gb, seeders, true, Config));

    [Fact]
    public void Cached_IsNotAutoApproved_WhenOptionDisabled()
    {
        var cfg = new PluginConfiguration { AutoApproveWhenCached = false, MovieMaxSizeGb = 15, MinSeedersForAutoApproval = 5 };
        Assert.Equal(AutoApprovalReason.None, ApprovalRules.Evaluate(RequestScope.Movie, 40 * Gb, 50, true, cfg));
    }

    [Theory]
    [InlineData(RequestScope.Movie, 15, 5, AutoApprovalReason.Size)]
    [InlineData(RequestScope.Movie, 16, 50, AutoApprovalReason.None)]
    [InlineData(RequestScope.Movie, 2, 4, AutoApprovalReason.None)]
    [InlineData(RequestScope.Episode, 5, 10, AutoApprovalReason.Size)]
    [InlineData(RequestScope.Episode, 6, 10, AutoApprovalReason.None)]
    [InlineData(RequestScope.Season, 30, 10, AutoApprovalReason.Size)]
    [InlineData(RequestScope.Season, 31, 10, AutoApprovalReason.None)]
    public void SizeAndSeeders_UsePerScopeThreshold(RequestScope scope, long sizeGb, int seeders, AutoApprovalReason expected)
        => Assert.Equal(expected, ApprovalRules.Evaluate(scope, sizeGb * Gb, seeders, false, Config));

    [Fact]
    public void ZeroThreshold_DisablesSizeRule()
    {
        var cfg = new PluginConfiguration { MovieMaxSizeGb = 0, MinSeedersForAutoApproval = 0 };
        Assert.Equal(AutoApprovalReason.None, ApprovalRules.Evaluate(RequestScope.Movie, 1, 100, false, cfg));
    }

    [Fact]
    public void UnknownSize_IsNeverAutoApprovedBySize()
        => Assert.Equal(AutoApprovalReason.None, ApprovalRules.Evaluate(RequestScope.Movie, 0, 100, false, Config));
}
