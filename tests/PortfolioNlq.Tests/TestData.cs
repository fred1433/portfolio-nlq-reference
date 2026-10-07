using PortfolioNlq.Cli;
using PortfolioNlq.Fixture;
using PortfolioNlq.Interpretation;

namespace PortfolioNlq.Tests;

public static class TestData
{
    public static readonly FixtureSet Fixture = FixtureSet.Load(Repo.FixtureDir);
    public static readonly ReferenceClock Clock = ReferenceClock.Parse("2026-10-07T09:00:00-05:00", "America/Chicago");
    public static DirectorySnapshot Directory(int tenant) =>
        new FixtureEntityDirectory(Fixture).LoadAsync(new(tenant, "test"), default).Result;
}
