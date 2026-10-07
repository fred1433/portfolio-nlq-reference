using System.Globalization;

namespace PortfolioNlq.Interpretation;

/// <summary>
/// The single clock every relative date is resolved against, once, before any SQL exists.
/// It is recorded in the trace, so "yesterday" means the same day when the trace is replayed next year.
/// </summary>
public sealed record ReferenceClock(DateTimeOffset Now, string TimeZone)
{
    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);

    public static ReferenceClock Parse(string iso, string timeZone) =>
        new(DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture), timeZone);

    /// <summary>Returns the calendar date a token stands for, or null when the token is unknown.</summary>
    public DateOnly? Resolve(string token, IReadOnlyList<DateOnly> closes)
    {
        var t = token.Trim().ToLowerInvariant();
        switch (t)
        {
            case "today": return Today;
            case "yesterday": return Today.AddDays(-1);
            case "last_close":
                var before = closes.Where(c => c < Today).ToList();
                return before.Count == 0 ? null : before.Max();
            case "last_friday": return MostRecent(DayOfWeek.Friday);
            case "previous_month_end": return new DateOnly(Today.Year, Today.Month, 1).AddDays(-1);
        }
        return Enum.TryParse<DayOfWeek>(t, ignoreCase: true, out var dow) && dow is not DayOfWeek.Saturday and not DayOfWeek.Sunday
            ? MostRecent(dow) : null;
    }

    DateOnly MostRecent(DayOfWeek day)
    {
        var d = Today.AddDays(-1);
        while (d.DayOfWeek != day) d = d.AddDays(-1);
        return d;
    }
}
