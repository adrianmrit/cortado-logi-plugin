namespace Cortado.Core;

public static class LocalTime
{
    public static DateTimeOffset NextOccurrence(DateTimeOffset now, TimeZoneInfo zone, Int32 hour, Int32 minute)
    {
        if (hour is < 0 or > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(hour), "Choose an hour from 0-23.");
        }

        if (minute is < 0 or > 59)
        {
            throw new ArgumentOutOfRangeException(nameof(minute), "Choose a minute from 0-59.");
        }

        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        for (var day = 0; day < 2; day++)
        {
            var local = DateTime.SpecifyKind(today.AddDays(day).AddHours(hour).AddMinutes(minute), DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(local))
            {
                if (local < TimeZoneInfo.ConvertTime(now, zone).DateTime)
                {
                    continue;
                }

                throw new ArgumentException("That local time does not exist because the clock moves forward. Choose another time.");
            }
            var offsets = zone.IsAmbiguousTime(local)
                ? zone.GetAmbiguousTimeOffsets(local)
                : [zone.GetUtcOffset(local)];
            var candidates = offsets.Select(offset => new DateTimeOffset(local, offset))
                .Where(candidate => candidate > now).OrderBy(candidate => candidate).ToArray();
            if (candidates.Length > 0)
            {
                return candidates[0].ToUniversalTime();
            }
        }
        throw new ArgumentException("No future occurrence of that local time is available.");
    }
}