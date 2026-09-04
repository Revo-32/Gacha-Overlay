namespace GachaOverlay.Core.Gta;

public sealed class KstResetSchedule
{
    public static readonly TimeSpan DailyResetTime = TimeSpan.FromHours(15);
    public static readonly TimeSpan WeeklyResetTime = TimeSpan.FromHours(18);
    public static readonly TimeSpan WeeklyPreparationGrace = TimeSpan.FromHours(6);

    public KstResetSchedule(TimeZoneInfo? timeZone = null)
    {
        TimeZone = timeZone ?? ResolveKoreaTimeZone();
    }

    public TimeZoneInfo TimeZone { get; }

    public DateTimeOffset ToKst(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc.ToUniversalTime(), TimeZone);

    public DateTimeOffset GetDailyCycleStart(DateTimeOffset utc)
    {
        var local = ToKst(utc);
        var date = local.TimeOfDay >= DailyResetTime ? local.Date : local.Date.AddDays(-1);
        return AtKst(date, DailyResetTime);
    }

    public DateTimeOffset GetNextDailyReset(DateTimeOffset utc) =>
        GetDailyCycleStart(utc).AddDays(1);

    public DateTimeOffset GetWeeklyCycleStart(DateTimeOffset utc)
    {
        var local = ToKst(utc);
        var daysSinceThursday = ((int)local.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7;
        var date = local.Date.AddDays(-daysSinceThursday);
        if (daysSinceThursday == 0 && local.TimeOfDay < WeeklyResetTime)
        {
            date = date.AddDays(-7);
        }

        return AtKst(date, WeeklyResetTime);
    }

    public DateTimeOffset GetNextWeeklyReset(DateTimeOffset utc) =>
        GetWeeklyCycleStart(utc).AddDays(7);

    public string GetDailyCycleKey(DateTimeOffset utc) =>
        GetDailyCycleStart(utc).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public string GetWeeklyCycleKey(DateTimeOffset utc) =>
        GetWeeklyCycleStart(utc).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public DateTimeOffset AtKst(DateTime date, TimeSpan time)
    {
        var unspecified = DateTime.SpecifyKind(date.Date.Add(time), DateTimeKind.Unspecified);
        return new DateTimeOffset(unspecified, TimeZone.GetUtcOffset(unspecified));
    }

    public static string FormatCountdown(DateTimeOffset target, DateTimeOffset now)
    {
        var remaining = target - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "0분";
        }

        var totalMinutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        if (totalMinutes >= 24 * 60)
        {
            return $"{totalMinutes / (24 * 60)}일 {(totalMinutes % (24 * 60)) / 60:00}시간";
        }

        return totalMinutes >= 60
            ? $"{totalMinutes / 60:00}시간 {totalMinutes % 60:00}분"
            : $"{totalMinutes}분";
    }

    public static TimeZoneInfo ResolveKoreaTimeZone()
    {
        foreach (var id in new[] { "Asia/Seoul", "Korea Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "LSOverlay-KST",
            TimeSpan.FromHours(9),
            "Korea Standard Time",
            "Korea Standard Time");
    }
}
