using System.Globalization;
using ClosedXML.Excel;
using PortfolioNlq.Audit;

namespace PortfolioNlq.Export;

/// <summary>
/// Writes a workbook from a stored trace, without running anything again: a Result sheet with typed numbers and units,
/// the contributing lines when there are any, and an Audit sheet carrying the trace identity.
/// </summary>
public static class ExcelExporter
{
    public static readonly IReadOnlyDictionary<string, (string Header, string Format)> Columns = new Dictionary<string, (string, string)>
    {
        ["account_number"] = ("Account", "@"), ["account_name"] = ("Account name", "@"), ["household"] = ("Household", "@"),
        ["custodian"] = ("Custodian", "@"), ["model"] = ("Model", "@"), ["account_count"] = ("Accounts", "0"),
        ["total_mv_usd"] = ("Market value (USD)", "#,##0.00"), ["weight_pct"] = ("Weight (%)", "0.00"),
        ["target_pct"] = ("Target (%)", "0.00"), ["drift_pts"] = ("Drift (pts)", "+0.00;-0.00;0.00"),
        ["row_status"] = ("Status", "@"), ["missing_detail"] = ("Why incomplete", "@"),
        ["symbol"] = ("Security", "@"), ["security_name"] = ("Security name", "@"), ["block_id"] = ("Block", "0"), ["side"] = ("Side", "@"),
        ["allocated_qty"] = ("Allocated (units)", "#,##0"), ["executed_qty"] = ("Executed (units)", "#,##0"),
        ["cancelled_qty"] = ("Cancelled (units)", "#,##0"), ["remaining_qty"] = ("Remaining (units)", "#,##0"), ["expired_qty"] = ("Expired (units)", "#,##0"),
        ["allocation_id"] = ("Allocation", "0"), ["time_in_force"] = ("Time in force", "@"), ["line_status"] = ("Status", "@"),
        ["order_status"] = ("Order status", "@"), ["allocation_count"] = ("Allocations", "0"),
        ["quantity"] = ("Quantity", "#,##0.00"), ["currency"] = ("Currency", "@"), ["close_price"] = ("Close (local)", "#,##0.00"),
        ["usd_per_unit"] = ("FX (USD per currency unit)", "0.0000"), ["market_value_usd"] = ("Market value (USD)", "#,##0.00"),
        ["contribution_pct"] = ("Share of account (%)", "0.00"), ["missing_reason"] = ("Missing", "@"),
    };

    public static void Write(AnswerTrace t, string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Result");
        Text(ws.Cell(1, 1), t.Question); ws.Cell(1, 1).Style.Font.Bold = true;
        var q = t.Interpretation;
        Text(ws.Cell(2, 1), q is null ? "Not answered" : q.Measure == "drift"
            ? $"As of the {q.AsOf:yyyy-MM-dd} close, {q.DateBasis}-date positions, USD"
            : $"Order book as at {q.AsOfTime:yyyy-MM-dd HH:mm} {(string.IsNullOrEmpty(t.TimeZone) ? "America/Chicago" : t.TimeZone)}, quantities in units");
        Text(ws.Cell(3, 1), $"Recorded run {t.RunId}, synthetic data, fixture {t.Fixture.Id}. Status: {t.ExecutionStatus}.");
        WriteTable(ws, 5, t.Columns, t.Rows);
        ws.Columns().AdjustToContents(5, 60);

        if (t.DetailRows.Count > 0)
        {
            var dws = wb.AddWorksheet("Contributing lines");
            WriteTable(dws, 1, t.DetailRows[0].Keys.ToList(), t.DetailRows);
            dws.Columns().AdjustToContents(1, 60);
        }

        var a = wb.AddWorksheet("Audit");
        var rows = new List<(string, string?)>
        {
            ("Run", t.RunId), ("Case", t.CaseId), ("Question", t.Question), ("Outcome", t.Outcome), ("Execution status", t.ExecutionStatus),
            ("Execution note", t.ExecutionNote), ("Scope", t.Scope), ("Tenant", t.TenantId.ToString(CultureInfo.InvariantCulture)),
            ("Reference clock", $"{t.ReferenceClock} ({t.TimeZone})"), ("As of", q?.AsOf?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            ("Date basis", q?.DateBasis), ("Definition", q?.Definition), ("Defaults applied", q is null ? null : string.Join("; ", q.DefaultsApplied)),
            ("Model", $"{t.Model.Provider}, {t.Model.ModelId}, {t.Model.Mode}"), ("Recording", t.Model.RecordingId),
            ("Prompt", $"{t.Model.PromptVersion} sha256 {t.Model.PromptSha256}"), ("Model reply (raw)", t.Model.RawResponse),
            ("Catalog", t.Versions.Catalog), ("Compiler", t.Versions.Compiler), ("Schema", t.Versions.Schema), ("App", t.Versions.App),
            ("Fixture", $"{t.Fixture.Id} sha256 {t.Fixture.Sha256}"), ("Executed on", t.ExecutedOn), ("Executed at", t.ExecutedAt),
            ("Parameters", string.Join("; ", t.Parameters.Select(p => $"{p.Name} {p.SqlType} = {p.Value}"))), ("SQL", t.Sql),
            ("Trace content sha256", t.ContentSha256()),
        };
        for (var i = 0; i < rows.Count; i++)
        {
            Text(a.Cell(i + 1, 1), rows[i].Item1); a.Cell(i + 1, 1).Style.Font.Bold = true;
            Text(a.Cell(i + 1, 2), rows[i].Item2 ?? "");
            a.Cell(i + 1, 2).Style.Alignment.WrapText = true;
        }
        a.Column(1).Width = 24; a.Column(2).Width = 110;
        wb.SaveAs(path);
    }

    static void WriteTable(IXLWorksheet ws, int top, IReadOnlyList<string> cols, IReadOnlyList<Dictionary<string, string?>> rows)
    {
        for (var c = 0; c < cols.Count; c++)
        {
            var header = Columns.TryGetValue(cols[c], out var m) ? m.Header : cols[c];
            Text(ws.Cell(top, c + 1), header);
            ws.Cell(top, c + 1).Style.Font.Bold = true;
        }
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < cols.Count; c++)
            {
                var cell = ws.Cell(top + 1 + r, c + 1);
                var raw = rows[r].GetValueOrDefault(cols[c]);
                var format = Columns.TryGetValue(cols[c], out var m) ? m.Format : "@";
                if (raw is null) continue;
                if (format != "@" && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                {
                    cell.Value = number;
                    // share and unit counts are whole numbers; only cash balances carry cents
                    cell.Style.NumberFormat.Format = cols[c] == "quantity" && number == decimal.Truncate(number) ? "#,##0" : format;
                }
                else Text(cell, raw);
            }
    }

    /// <summary>Text from the database is stored as text; anything a spreadsheet could read as a formula gets a quote prefix.</summary>
    public static void Text(IXLCell cell, string value)
    {
        cell.Value = value;
        cell.Style.NumberFormat.Format = "@";
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) cell.Style.IncludeQuotePrefix = true;
    }
}
