using ClosedXML.Excel;
using PortfolioNlq.Audit;
using PortfolioNlq.Catalog;
using PortfolioNlq.Export;

namespace PortfolioNlq.Tests;

public class ExcelTests
{
    [Fact]
    public void Text_from_the_database_stays_text_and_numbers_stay_numbers()
    {
        var t = new AnswerTrace
        {
            RunId = "test", CaseId = "T1", Question = "=cmd|' /C calc'!A0",
            Columns = ["account_number", "account_name", "drift_pts"],
            Rows = [new() { ["account_number"] = "LWP-1001", ["account_name"] = "=HYPERLINK(\"http://example.invalid\",\"click\")", ["drift_pts"] = "3.1085091" }],
        };
        var path = Path.Combine(Path.GetTempPath(), $"nlq-test-{Guid.NewGuid():N}.xlsx");
        ExcelExporter.Write(t, path);
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheet("Result");
        var name = ws.Cell(6, 2);
        Assert.False(name.HasFormula);
        Assert.Equal(XLDataType.Text, name.DataType);
        Assert.StartsWith("=HYPERLINK", name.GetString());
        Assert.True(name.Style.IncludeQuotePrefix);
        Assert.False(ws.Cell(1, 1).HasFormula);
        Assert.Equal(XLDataType.Number, ws.Cell(6, 3).DataType);
        Assert.Equal(3.1085091, ws.Cell(6, 3).GetDouble(), 7);
        Assert.NotNull(wb.Worksheet("Audit"));
        File.Delete(path);
    }
}

public class PromptTests
{
    [Fact]
    public void The_prompt_carries_no_schema_no_sql_and_no_tenant()
    {
        var p = PromptBuilder.SystemPrompt();
        Assert.DoesNotContain("dbo.", p);
        Assert.DoesNotContain("SELECT", p);
        Assert.DoesNotContain("tenant_id", p);
        Assert.DoesNotContain("LWP-", p);
    }
}
