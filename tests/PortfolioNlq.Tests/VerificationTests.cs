using PortfolioNlq.Cli;

namespace PortfolioNlq.Tests;

/// <summary>The database-free check a reviewer runs first: published replies, SQL and grades all reproduce.</summary>
public class VerificationTests
{
    [Fact]
    public async Task The_published_run_reproduces_without_a_database()
    {
        var published = File.ReadAllText(Path.Combine(Repo.Root, "runs", "PUBLISHED")).Trim().Split(' ');
        Assert.Equal(0, await Commands.Verify(Path.Combine(Repo.Root, published[0]), Path.Combine(Repo.Root, published[1])));
    }
}
