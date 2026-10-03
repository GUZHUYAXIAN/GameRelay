using System;
using System.Linq;

namespace GameRelay.Core
{
    public static class Planner
    {
        // Find the first occurrence after the durable cursor, bounded to a week.
        // Large offline gaps never enumerate all missed days.
        public static long? Next(Schedule schedule, long afterUtc)
        {
            if (schedule.Minutes.Count == 0 || schedule.Weekdays.Count == 0) return null;
            if (schedule.Minutes.Any(m => m < 0 || m >= 1440) || schedule.Weekdays.Any(d => d < 0 || d > 6))
                throw new ArgumentException("无效的计划时间或星期");
            var zone = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZoneId);
            var after = new DateTime(Math.Max(afterUtc, schedule.CreatedUtc), DateTimeKind.Utc);
            var date = TimeZoneInfo.ConvertTimeFromUtc(after, zone).Date;
            long? best = null;
            for (int day = 0; day < 9; day++)
            {
                var localDate = date.AddDays(day);
                if (!schedule.Weekdays.Contains((int)localDate.DayOfWeek)) continue;
                foreach (int minute in schedule.Minutes.Distinct().OrderBy(x => x))
                {
                    var local = DateTime.SpecifyKind(localDate.AddMinutes(minute), DateTimeKind.Unspecified);
                    if (zone.IsInvalidTime(local)) continue;
                    var utc = TimeZoneInfo.ConvertTimeToUtc(local, zone).Ticks;
                    if (utc > after.Ticks && (!best.HasValue || utc < best.Value)) best = utc;
                }
                if (best.HasValue) return best;
            }
            return null;
        }
    }
}
