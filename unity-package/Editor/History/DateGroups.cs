using System;

namespace AgenLink.History
{
    internal static class DateGroups
    {
        /// <summary>Bucket label for a date relative to now: Today / Yesterday / This week / Older.</summary>
        public static string Of(DateTime when, DateTime now)
        {
            DateTime d = when.Date, today = now.Date;
            if (d == today) return "Today";
            if (d == today.AddDays(-1)) return "Yesterday";
            if (d > today.AddDays(-7)) return "This week";
            return "Older";
        }

        /// <summary>Compact relative time for card headers: "just now", "5 min ago", "2 h ago",
        /// "yesterday", "3 days ago", then "Jun 4".</summary>
        public static string Rel(DateTime when, DateTime now)
        {
            TimeSpan d = now - when;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
            if (when.Date == now.Date) return (int)d.TotalHours + " h ago";
            int days = (int)(now.Date - when.Date).TotalDays;
            if (days == 1) return "yesterday";
            if (days < 7) return days + " days ago";
            return when.ToString("MMM d");
        }
    }
}
