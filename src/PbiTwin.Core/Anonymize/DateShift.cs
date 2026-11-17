namespace PbiTwin.Core.Anonymize;

/// <summary>Moves dates by the twin's shift.</summary>
internal static class DateShift
{
    /// <summary>
    /// Dates at the ends of the calendar are placeholders ("no end date" = 9999-12-31, "unknown" = 1900-01-01), not
    /// events. They carry no information, filters and DAX often compare with them, and shifting them would overflow.
    /// </summary>
    public static bool IsPlaceholder(DateTime d) => d.Year >= 9990 || d.Year <= 1900;

    public static DateTime Apply(DateTime d, int days) => IsPlaceholder(d) ? d : d.AddDays(days);
}
