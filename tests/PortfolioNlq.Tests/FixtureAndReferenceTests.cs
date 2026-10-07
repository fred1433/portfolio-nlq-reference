using PortfolioNlq.Fixture;
using PortfolioNlq.Reference;

namespace PortfolioNlq.Tests;

/// <summary>The traps the fixture is meant to contain are really there.</summary>
public class FixtureTests
{
    static readonly FixtureSet F = TestData.Fixture;

    [Fact]
    public void Every_execution_is_booked_as_a_trade_and_a_cash_movement_settling_next_business_day()
    {
        foreach (var e in F.Executions)
        {
            var txns = F.Transactions.Where(t => t.ExecutionId == e.ExecutionId).ToList();
            Assert.Equal(2, txns.Count);
            Assert.All(txns, t => Assert.Equal(DateOnly.FromDateTime(e.ExecutedAt), t.TradeDate));
            Assert.All(txns, t => Assert.True(t.SettleDate > t.TradeDate));
            Assert.Equal(e.Quantity, Math.Abs(txns.Single(t => !t.Type.StartsWith("Cash", StringComparison.Ordinal)).Quantity));
        }
    }

    [Fact]
    public void The_traps_are_present()
    {
        Assert.Contains(F.BlockOrders, b => F.Allocations.Where(a => a.BlockId == b.BlockId).Sum(a => a.AllocatedQuantity) != b.OrderQuantity);
        Assert.Contains(F.Allocations, a => F.Executions.Count(e => e.AllocationId == a.AllocationId) >= 2);
        Assert.Contains(F.Allocations, a => a.CancelledQuantity > 0 && F.Executions.Any(e => e.AllocationId == a.AllocationId));
        Assert.Contains(F.SecurityTags.GroupBy(t => t.SecurityId), g => g.Count() >= 3);
        Assert.DoesNotContain(F.Prices, p => p.SecurityId == 8 && p.PriceDate == new DateOnly(2026, 10, 6));
        Assert.DoesNotContain(F.FxRates, r => r.Currency == "EUR" && r.RateDate == new DateOnly(2026, 10, 2));
        Assert.Contains(F.Accounts.GroupBy(a => a.Name), g => g.Select(a => a.TenantId).Distinct().Count() == 2);
        Assert.Contains(F.Transactions, t => t.SettleDate != t.TradeDate);
        Assert.Contains(F.Accounts, a => a.AccountType == "Fund");
    }

    [Fact]
    public void Model_hierarchy_has_two_levels_and_each_model_sums_to_one()
    {
        Assert.Contains(F.ModelComponents, c => c.ChildModelId is not null);
        foreach (var g in F.ModelComponents.GroupBy(c => c.ModelId)) Assert.Equal(1m, g.Sum(c => c.Weight));
    }

    [Fact]
    public void The_fixture_hash_is_stable() => Assert.Equal(FixtureSet.ComputeSha256(Cli.Repo.FixtureDir), F.Sha256);
}

/// <summary>Perturbations that must not move an answer, checked on the independent reference.</summary>
public class ReferencePerturbationTests
{
    static EvalCase Case(string id) => EvalFile.Load(Cli.Repo.CasesFile).Cases.Single(c => c.Id == id);
    static string Rows(FixtureSet f, string id) => System.Text.Json.JsonSerializer.Serialize(new ReferenceCalculator(f).Compute(Case(id)).Rows);

    [Fact]
    public void Adding_a_tag_does_not_move_an_asset_class_drift_or_another_tag()
    {
        var f = TestData.Fixture with { SecurityTags = [.. TestData.Fixture.SecurityTags, new SecurityTag(1, "ESG Screened")] };
        Assert.Equal(Rows(TestData.Fixture, "A01"), Rows(f, "A01"));
        Assert.Equal(Rows(TestData.Fixture, "A04"), Rows(f, "A04"));
    }

    [Fact]
    public void Splitting_a_fill_in_two_does_not_move_the_executed_quantity()
    {
        var f0 = TestData.Fixture;
        var e = f0.Executions.Single(x => x.ExecutionId == 2);
        var f = f0 with
        {
            Executions = [.. f0.Executions.Where(x => x.ExecutionId != 2), e with { Quantity = 120 }, e with { ExecutionId = 99, Quantity = 80 }],
        };
        Assert.Equal(Rows(f0, "B01"), Rows(f, "B01"));
    }

    [Fact]
    public void Another_tenants_allocation_does_not_move_this_tenants_answer()
    {
        var f0 = TestData.Fixture;
        var f = f0 with { Allocations = [.. f0.Allocations, new Allocation(9999, 905, 203, 75, 0, null)] };
        Assert.Equal(Rows(f0, "X02"), Rows(f, "X02"));
        Assert.Equal(Rows(f0, "B01"), Rows(f, "B01"));
    }
}
