using System.Globalization;


namespace SlimFaas.Jobs;

public static class Cron
{
    public static ResultWithError<long> GetLatestJobExecutionTimestamp(string cronDefinition, long currentTimestamp)
    {

        if (string.IsNullOrWhiteSpace(cronDefinition))
            return new ResultWithError<long>(0, new ErrorResult("cron_definition", "Cron definition must not be empty"));

        var parts = cronDefinition.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return new ResultWithError<long>(0, new ErrorResult("cron_definition", "Cron definition must have exactly 5 fields"));

        var minuteSet      = ParseCronField(parts[0], 0, 59);
        var hourSet        = ParseCronField(parts[1], 0, 23);
        var dayOfMonthSet  = ParseCronField(parts[2], 1, 31);
        var monthSet       = ParseCronField(parts[3], 1, 12);
        var dayOfWeekSet   = ParseCronField(parts[4], 0, 6); // 0 = Sunday

        var now = DateTimeOffset.FromUnixTimeSeconds(currentTimestamp).UtcDateTime;
        now = now.AddSeconds(-now.Second); // arrondi à la minute

        for (int i = 0; i < 366 * 24 * 60; i++) // Limite : 1 an en arrière max
        {
            // On descend minute par minute
            var candidate = now.AddMinutes(-i);

            if (!monthSet.Contains(candidate.Month))
                continue;

            bool domMatch = dayOfMonthSet.Contains(candidate.Day);
            bool dowMatch = dayOfWeekSet.Contains((int)candidate.DayOfWeek);
            bool domStar = parts[2] == "*";
            bool dowStar = parts[4] == "*";
            bool dayMatch;
            if (!domStar && !dowStar)
                dayMatch = domMatch || dowMatch;
            else if (!domStar)
                dayMatch = domMatch;
            else if (!dowStar)
                dayMatch = dowMatch;
            else
                dayMatch = true;

            if (!dayMatch)
                continue;

            if (!hourSet.Contains(candidate.Hour))
                continue;

            if (!minuteSet.Contains(candidate.Minute))
                continue;

            return new ResultWithError<long>(new DateTimeOffset(candidate, TimeSpan.Zero).ToUnixTimeSeconds());
        }

        return new ResultWithError<long>(0,
            new ErrorResult("cron_definition", "No previous cron occurrence found in the past year."));
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
            if (part.Contains("/"))
            {
                var split = part.Split('/');
                var range = split[0];
                var step = int.Parse(split[1], CultureInfo.InvariantCulture);

                int rangeStart = min, rangeEnd = max;
                if (range != "*")
                {
                    if (range.Contains("-"))
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
            else if (part.Contains("-"))
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
