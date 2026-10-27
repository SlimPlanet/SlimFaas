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

        return new ResultWithError<CronExpression>(new CronExpression(
            ParseCronField(parts[0], 0, 59),
            ParseCronField(parts[1], 0, 23),
            ParseCronField(parts[2], 1, 31),
            ParseCronField(parts[3], 1, 12),
            ParseCronField(parts[4], 0, 6), // 0 = Sunday
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

    // Parse un champ cron (ex: "5", "1-5", "*/15", "1,2,3")
    private static HashSet<int> ParseCronField(string field, int min, int max)
    {
        var values = new HashSet<int>();
        if (field == "*")
        {
            for (int i = min; i <= max; i++)
                values.Add(i);
            return values;
        }

        foreach (var part in field.Split(','))
        {
            if (part.Contains('/', StringComparison.Ordinal))
            {
                var split = part.Split('/');
                var range = split[0];
                var step = int.Parse(split[1], CultureInfo.InvariantCulture);

                int rangeStart = min, rangeEnd = max;
                // Treat "0" as "*" for step expressions (e.g., "0/15" = "*/15")
                if (range != "*" && range != "0")
                {
                    if (range.Contains('-', StringComparison.Ordinal))
                    {
                        var bounds = range.Split('-');
                        rangeStart = int.Parse(bounds[0], CultureInfo.InvariantCulture);
                        rangeEnd = int.Parse(bounds[1], CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        rangeStart = rangeEnd = int.Parse(range, CultureInfo.InvariantCulture);
                    }
                }
                for (int i = rangeStart; i <= rangeEnd; i += step)
                    values.Add(i);
            }
            else if (part.Contains('-', StringComparison.Ordinal))
            {
                var bounds = part.Split('-');
                int start = int.Parse(bounds[0], CultureInfo.InvariantCulture);
                int end = int.Parse(bounds[1], CultureInfo.InvariantCulture);
                for (int i = start; i <= end; i++)
                    values.Add(i);
            }
            else
            {
                values.Add(int.Parse(part, CultureInfo.InvariantCulture));
            }
        }
        return values;
    }
}
