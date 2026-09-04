using System.Globalization;
using System.Text.RegularExpressions;

namespace GachaOverlay.Core.Gta;

public static partial class GtaEventDateParser
{
    private static readonly IReadOnlyDictionary<string, int> Months =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["JAN"] = 1,
            ["JANUARY"] = 1,
            ["FEB"] = 2,
            ["FEBRUARY"] = 2,
            ["MAR"] = 3,
            ["MARCH"] = 3,
            ["APR"] = 4,
            ["APRIL"] = 4,
            ["MAY"] = 5,
            ["JUN"] = 6,
            ["JUNE"] = 6,
            ["JUL"] = 7,
            ["JULY"] = 7,
            ["AUG"] = 8,
            ["AUGUST"] = 8,
            ["SEP"] = 9,
            ["SEPT"] = 9,
            ["SEPTEMBER"] = 9,
            ["OCT"] = 10,
            ["OCTOBER"] = 10,
            ["NOV"] = 11,
            ["NOVEMBER"] = 11,
            ["DEC"] = 12,
            ["DECEMBER"] = 12,
        };

    public static IReadOnlyList<GtaEventDateRange> FindRanges(
        string? value,
        DateTimeOffset referenceUtc,
        KstResetSchedule? schedule = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<GtaEventDateRange>();
        }

        schedule ??= new KstResetSchedule();
        var localReference = schedule.ToKst(referenceUtc);
        var result = new List<GtaEventDateRange>();
        foreach (Match match in DateRangeRegex().Matches(GtaEventTextNormalizer.Normalize(value)))
        {
            if (!TryMonth(match.Groups["m1"].Value, out var startMonth) ||
                !int.TryParse(match.Groups["d1"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var startDay))
            {
                continue;
            }

            var endMonth = TryMonth(match.Groups["m2"].Value, out var parsedEndMonth)
                ? parsedEndMonth
                : startMonth;
            if (!int.TryParse(match.Groups["d2"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var endDay))
            {
                continue;
            }

            var startYear = InferStartYear(localReference.Date, startMonth, startDay);
            var endYear = endMonth < startMonth ? startYear + 1 : startYear;
            if (!TryDate(startYear, startMonth, startDay, out var startDate) ||
                !TryDate(endYear, endMonth, endDay, out var endDate) ||
                endDate < startDate || endDate - startDate > TimeSpan.FromDays(62))
            {
                continue;
            }

            result.Add(new GtaEventDateRange(
                schedule.AtKst(startDate, KstResetSchedule.WeeklyResetTime),
                schedule.AtKst(endDate.AddDays(1), KstResetSchedule.WeeklyResetTime),
                match.Value));
        }

        return result
            .DistinctBy(range => (range.StartAt, range.EndAt))
            .OrderBy(range => range.StartAt)
            .ToArray();
    }

    public static bool TryFindFirstRange(
        string? value,
        DateTimeOffset referenceUtc,
        out GtaEventDateRange? range)
    {
        range = FindRanges(value, referenceUtc).FirstOrDefault();
        return range is not null;
    }

    private static int InferStartYear(DateTime reference, int month, int day)
    {
        if (!TryDate(reference.Year, month, day, out var candidate))
        {
            return reference.Year;
        }

        if (candidate - reference > TimeSpan.FromDays(183))
        {
            return reference.Year - 1;
        }

        if (reference - candidate > TimeSpan.FromDays(183))
        {
            return reference.Year + 1;
        }

        return reference.Year;
    }

    private static bool TryMonth(string value, out int month) =>
        Months.TryGetValue(value.Trim(), out month);

    private static bool TryDate(int year, int month, int day, out DateTime result)
    {
        try
        {
            result = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            result = default;
            return false;
        }
    }

    [GeneratedRegex(
        @"\b(?<m1>JAN(?:UARY)?|FEB(?:RUARY)?|MAR(?:CH)?|APR(?:IL)?|MAY|JUN(?:E)?|JUL(?:Y)?|AUG(?:UST)?|SEP(?:T(?:EMBER)?)?|OCT(?:OBER)?|NOV(?:EMBER)?|DEC(?:EMBER)?)\s+(?<d1>\d{1,2})\s*-\s*(?:(?<m2>JAN(?:UARY)?|FEB(?:RUARY)?|MAR(?:CH)?|APR(?:IL)?|MAY|JUN(?:E)?|JUL(?:Y)?|AUG(?:UST)?|SEP(?:T(?:EMBER)?)?|OCT(?:OBER)?|NOV(?:EMBER)?|DEC(?:EMBER)?)\s+)?(?<d2>\d{1,2})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DateRangeRegex();
}
