using System.Text.Json;
using System.Text.Json.Serialization;

namespace PortfolioNlq.Interpretation;

/// <summary>The typed query the model must return. Anything outside this shape is rejected, not repaired.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ModelOutput
{
    public string? Kind { get; set; }
    public string? Measure { get; set; }
    public DimensionOut? Dimension { get; set; }
    public ThresholdOut? Threshold { get; set; }
    public List<string>? GroupBy { get; set; }
    public string? DateBasis { get; set; }
    public DateOut? AsOf { get; set; }
    public string? Side { get; set; }
    public string? AssetClass { get; set; }
    public string? Status { get; set; }
    public List<FilterOut>? Filters { get; set; }
    public string? Message { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DimensionOut { public string? Type { get; set; } public string? Value { get; set; } }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ThresholdOut { public string? Direction { get; set; } public decimal? Points { get; set; } }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DateOut { public string? Date { get; set; } public int? Month { get; set; } public int? Day { get; set; } public string? Relative { get; set; } public string? Time { get; set; } }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class FilterOut { public string? Field { get; set; } public string? Value { get; set; } }

public static class ModelOutputParser
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    /// <summary>Accepts one JSON object, optionally wrapped in a single code fence. Nothing else.</summary>
    public static (ModelOutput? Output, string? Error) Parse(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewLine = text.IndexOf('\n');
            if (firstNewLine < 0 || !text.EndsWith("```", StringComparison.Ordinal)) return (null, "unterminated code fence");
            text = text[(firstNewLine + 1)..^3].Trim();
        }
        if (!text.StartsWith('{') || !text.EndsWith('}')) return (null, "reply is not a single JSON object");
        try
        {
            var output = JsonSerializer.Deserialize<ModelOutput>(text, Options);
            return output is null ? (null, "empty JSON") : (output, null);
        }
        catch (JsonException e)
        {
            return (null, "JSON outside the schema: " + e.Message.Split('.')[0]);
        }
    }
}
