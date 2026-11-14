using System.Globalization;
using NodaTime;


namespace SlimFaas.Jobs;

public static class Cron
{
    public const string InvalidTimeZone = "invalid_timezone";

    private const int SearchWindowInMinutes = 366 * 24 * 60;

    public static ResultWithError<long> GetNextJobExecutionTimestamp(string cronDefinition, long currentTimestamp,
        string? timeZone = null)
    {
        var parsed = Parse(cronDefinition, timeZone);
        if (parsed.Data == null)
            return new ResultWithError<long>(0, parsed.Error);

        var start = currentTimestamp - currentTimestamp % 60 + 60; // start from next minute

        for (int i = 0; i < SearchWindowInMinutes; i++)
        {
            var candidate = start + i * 60L;
            if (parsed.Data.Matches(candidate))
                return new ResultWithError<long>(candidate);
        }

        return new ResultWithError<long>(0,
            new ErrorResult("cron_definition", "No next cron occurrence found in the next year."));
    }

    public static ResultWithError<long> GetLatestJobExecutionTimestamp(string cronDefinition, long currentTimestamp,
        string? timeZone = null)
    {
        var parsed = Parse(cronDefinition, timeZone);
        if (parsed.Data == null)
            return new ResultWithError<long>(0, parsed.Error);

        var start = currentTimestamp - currentTimestamp % 60; // arrondi à la minute

        for (int i = 0; i < SearchWindowInMinutes; i++) // Limite : 1 an en arrière max
        {
            // On descend minute par minute
            var candidate = start - i * 60L;
            if (parsed.Data.Matches(candidate))
                return new ResultWithError<long>(candidate);
        }

        return new ResultWithError<long>(0,
            new ErrorResult("cron_definition", "No previous cron occurrence found in the past year."));
    }

    // A missing time zone keeps the historical behaviour: schedules are evaluated in UTC.
    private static ResultWithError<DateTimeZone> GetTimeZone(string? timeZone)
    {
        if (string.IsNullOrEmpty(timeZone))
            return new ResultWithError<DateTimeZone>(DateTimeZone.Utc);

        var zone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZone);
        return zone == null
            ? new ResultWithError<DateTimeZone>(null,
                new ErrorResult(InvalidTimeZone, $"The timezone '{timeZone}' is not a valid IANA timezone."))
            : new ResultWithError<DateTimeZone>(zone);
    }

    private static ResultWithError<CronExpression> Parse(string cronDefinition, string? timeZone)
    {
        if (string.IsNullOrWhiteSpace(cronDefinition))
            return new ResultWithError<CronExpression>(null, new ErrorResult("cron_definition", "Cron definition must not be empty"));

        var parts = cronDefinition.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return new ResultWithError<CronExpression>(null, new ErrorResult("cron_definition", "Cron definition must have exactly 5 fields"));

        var zone = GetTimeZone(timeZone);
        if (zone.Data == null)
            return new ResultWithError<CronExpression>(null, zone.Error);

        var minutes = ParseCronField(parts[0], 0, 59);
        var hours = ParseCronField(parts[1], 0, 23);
        var daysOfMonth = ParseCronField(parts[2], 1, 31);
        var months = ParseCronField(parts[3], 1, 12);
        var daysOfWeek = ParseCronField(parts[4], 0, 6, isDayOfWeek: true); // 0 = Sunday
        if (minutes == null || hours == null || daysOfMonth == null || months == null || daysOfWeek == null)
            return new ResultWithError<CronExpression>(null, new ErrorResult("cron_definition",
                "Cron fields must be values, ranges, lists or positive steps within minute 0-59, hour 0-23, " +
                "day of month 1-31, month 1-12 and day of week 0-7"));

        return new ResultWithError<CronExpression>(new CronExpression(
            minutes,
            hours,
            daysOfMonth,
            months,
            daysOfWeek,
            parts[2] == "*",
            parts[4] == "*",
            zone.Data));
    }

    private sealed record CronExpression(
        HashSet<int> Minutes,
        HashSet<int> Hours,
        HashSet<int> DaysOfMonth,
        HashSet<int> Months,
        HashSet<int> DaysOfWeek,
        bool DayOfMonthStar,
        bool DayOfWeekStar,
        DateTimeZone TimeZone)
    {
        // Fields are matched against the wall-clock time of the instant in the schedule time zone.
        // A local time skipped by a daylight-saving transition never occurs, so it never matches;
        // a local time repeated by a transition occurs twice, so both instants match.
        public bool Matches(long timestamp)
        {
            var candidate = Instant.FromUnixTimeSeconds(timestamp).InZone(TimeZone).LocalDateTime;

            if (!Months.Contains(candidate.Month))
                return false;

            bool domMatch = DaysOfMonth.Contains(candidate.Day);
            bool dowMatch = DaysOfWeek.Contains((int)candidate.DayOfWeek % 7); // IsoDayOfWeek.Sunday = 7
            bool dayMatch;
            if (!DayOfMonthStar && !DayOfWeekStar)
                dayMatch = domMatch || dowMatch;
            else if (!DayOfMonthStar)
                dayMatch = domMatch;
            else if (!DayOfWeekStar)
                dayMatch = dowMatch;
            else
                dayMatch = true;

            return dayMatch && Hours.Contains(candidate.Hour) && Minutes.Contains(candidate.Minute);
        }
    }

    // Parses a cron field (e.g. "5", "1-5", "*/15", "5/15", "1,2,3").
    // "N/step" runs from N to the end of the field range, as in Kubernetes CronJob schedules.
    // Returns null when the field is malformed or a value is outside [min, max].
    // In the day-of-week field, an explicit 7 is also accepted as Sunday.
    private static HashSet<int>? ParseCronField(string field, int min, int max, bool isDayOfWeek = false)
    {
        var explicitMax = isDayOfWeek ? 7 : max;
        var values = new HashSet<int>();
        foreach (var part in field.Split(','))
        {
            var stepSeparator = part.IndexOf('/', StringComparison.Ordinal);
            var range = stepSeparator < 0 ? part : part[..stepSeparator];
            var step = 1;
            if (stepSeparator >= 0 && (!TryParseValue(part[(stepSeparator + 1)..], out step) || step == 0))
                return null;

            int rangeStart, rangeEnd;
            // Treat "0" as "*" for step expressions (e.g., "0/15" = "*/15"), even in fields starting at 1
            if (range == "*" || (stepSeparator >= 0 && range == "0"))
            {
                rangeStart = min;
                rangeEnd = max;
            }
            else
            {
                var rangeSeparator = range.IndexOf('-', StringComparison.Ordinal);
                if (rangeSeparator < 0)
                {
                    if (!TryParseValue(range, out rangeStart))
                        return null;
                    rangeEnd = stepSeparator < 0 ? rangeStart : max;
                }
                else if (!TryParseValue(range[..rangeSeparator], out rangeStart) ||
                         !TryParseValue(range[(rangeSeparator + 1)..], out rangeEnd))
                {
                    return null;
                }
            }

            if (rangeStart < min || rangeEnd > explicitMax || rangeStart > rangeEnd)
                return null;

            // A long counter cannot overflow, whatever the step.
            for (long i = rangeStart; i <= rangeEnd; i += step)
                values.Add(isDayOfWeek && i == 7 ? 0 : (int)i);
        }
        return values;
    }

    private static bool TryParseValue(string value, out int result) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
}
