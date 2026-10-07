using PortfolioNlq.Interpretation;

namespace PortfolioNlq.Tests;

public class ParserTests
{
    [Fact]
    public void Accepts_one_json_object_even_in_a_code_fence()
    {
        var (o, e) = ModelOutputParser.Parse("```json\n{\"kind\":\"refuse\",\"message\":\"no\"}\n```");
        Assert.Null(e);
        Assert.Equal("refuse", o!.Kind);
    }

    [Theory]
    [InlineData("Sure! Here is the query: {\"kind\":\"query\"}")]
    [InlineData("{\"kind\":\"query\",\"measure\":\"drift\",\"tenant_id\":2}")]
    [InlineData("{\"kind\":\"query\",\"sql\":\"SELECT * FROM dbo.account\"}")]
    [InlineData("{\"kind\":\"query\",\"filters\":[{\"field\":\"account\",\"value\":\"x\",\"tenant\":2}]}")]
    [InlineData("")]
    public void Rejects_anything_outside_the_shape(string raw)
    {
        var (o, e) = ModelOutputParser.Parse(raw);
        Assert.Null(o);
        Assert.NotNull(e);
    }
}

public class DateTests
{
    [Theory]
    [InlineData("yesterday", "2026-10-06")]
    [InlineData("tuesday", "2026-10-06")]
    [InlineData("last_friday", "2026-10-02")]
    [InlineData("last_close", "2026-10-06")]
    [InlineData("previous_month_end", "2026-09-30")]
    public void Relative_dates_resolve_once_against_the_recorded_clock(string token, string expected)
    {
        var closes = new[] { new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 6) };
        Assert.Equal(DateOnly.Parse(expected), TestData.Clock.Resolve(token, closes));
    }

    [Fact]
    public void Unknown_tokens_do_not_resolve() => Assert.Null(TestData.Clock.Resolve("since the rebalance", []));
}

public class ValidatorTests
{
    static ValidationOutcome V(string json, int tenant = 1) =>
        Validator.Validate(ModelOutputParser.Parse(json).Output!, TestData.Directory(tenant), TestData.Clock);

    [Fact]
    public void Approved_defaults_are_applied_and_listed()
    {
        var v = V("""{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"equity"}}""");
        Assert.Equal(OutcomeKind.Query, v.Kind);
        Assert.Equal("Equity", v.Query!.DimensionValue);
        Assert.Equal(new DateOnly(2026, 10, 6), v.Query.AsOf);
        Assert.Equal("trade", v.Query.DateBasis);
        Assert.Contains(v.Query.DefaultsApplied, d => d.Contains("USD"));
        Assert.Contains(v.Query.DefaultsApplied, d => d.Contains("trade-date"));
        Assert.Contains(v.Query.DefaultsApplied, d => d.Contains("2026-10-06"));
    }

    [Fact]
    public void A_date_without_a_recorded_close_is_a_question_not_the_nearest_date()
    {
        var v = V("""{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"Equity"},"as_of":{"relative":"previous_month_end"}}""");
        Assert.Equal(OutcomeKind.Clarify, v.Kind);
        Assert.Contains("2026-09-30", v.Message);
    }

    [Fact]
    public void A_name_matching_several_accounts_asks_which_one()
    {
        var v = V("""{"kind":"query","measure":"open_allocations","filters":[{"field":"account","value":"Okafor"}]}""");
        Assert.Equal(OutcomeKind.Clarify, v.Kind);
        Assert.Contains("LWP-1003", v.Message);
        Assert.Contains("LWP-1004", v.Message);
    }

    [Fact]
    public void A_name_outside_the_scope_returns_nothing_and_says_so()
    {
        var v = V("""{"kind":"query","measure":"open_allocations","filters":[{"field":"account","value":"Halyard Creek Offshore Ltd"}]}""");
        Assert.Equal(OutcomeKind.NoMatchInScope, v.Kind);
        Assert.Contains("not visible", v.Message!.Replace("is visible", "not visible"));
    }

    [Fact]
    public void The_same_name_resolves_to_the_asking_tenants_account()
    {
        Assert.Equal(101, V("""{"kind":"query","measure":"open_allocations","filters":[{"field":"account","value":"Whitfield Family Trust"}]}""", 1).Query!.Filters[0].Ids[0]);
        Assert.Equal(203, V("""{"kind":"query","measure":"open_allocations","filters":[{"field":"account","value":"Whitfield Family Trust"}]}""", 2).Query!.Filters[0].Ids[0]);
    }

    [Theory]
    [InlineData("""{"kind":"query","measure":"returns"}""")]
    [InlineData("""{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"Equity"},"side":"sell"}""")]
    [InlineData("""{"kind":"query","measure":"drift","dimension":{"type":"asset_class","value":"Equity"},"group_by":["security"]}""")]
    [InlineData("""{"kind":"query","measure":"open_allocations","filters":[{"field":"tenant","value":"2"}]}""")]
    [InlineData("""{"kind":"refuse"}""")]
    public void Unknown_or_mismatched_fields_are_rejected_not_repaired(string json) =>
        Assert.Equal(OutcomeKind.Rejected, V(json).Kind);

    [Fact]
    public void A_drift_without_a_sleeve_is_a_question() =>
        Assert.Equal(OutcomeKind.Clarify, V("""{"kind":"query","measure":"drift"}""").Kind);
}
