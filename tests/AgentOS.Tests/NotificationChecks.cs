using AgentOS.Core;

namespace AgentOS.Tests;

internal static class NotificationChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static Task RunAsync(string root)
    {
        var path = Path.Combine(root, "notifications.json");
        var center = new NotificationCenter(path);
        center.ConfigureQuietHours(new TimeOnly(22, 0), new TimeOnly(8, 0));
        var utc = TimeZoneInfo.Utc;
        Check(center.IsQuiet(new DateTimeOffset(2026, 1, 1, 23, 0, 0, TimeSpan.Zero), utc), "Late quiet hour was missed.");
        Check(center.IsQuiet(new DateTimeOffset(2026, 1, 2, 7, 59, 0, TimeSpan.Zero), utc), "Early quiet hour was missed.");
        Check(!center.IsQuiet(new DateTimeOffset(2026, 1, 2, 8, 0, 0, TimeSpan.Zero), utc), "Quiet hours included the end boundary.");
        var first = center.Record("event-1", "project-a", "work-1", null, "Completed", "  Finished\r\nnow  ", new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero));
        Check(first != null && first.Title == "Finished  now" && first.WorkId == "work-1", "Notice content was not retained or sanitized.");
        Check(center.Record("event-1", "PROJECT-A", "work-1", null, "Completed", "duplicate") == null, "Case variant duplicated a project event.");
        Check(center.Record("event-1", "project-b", null, "decision-1", "Decision", "Review") != null, "Distinct project event was suppressed.");
        var longTitle = new string('x', 120);
        Check(center.Record("event-2", "project-a", null, null, "Waiting", longTitle)!.Title.Length == 100, "Notice title was not bounded.");
        var reopened = new NotificationCenter(path);
        Check(reopened.History.Count == 3 && reopened.History[0].Identity == "event-1", "Notice history was not durable and ordered.");
        Check(reopened.QuietFrom == new TimeOnly(22, 0) && reopened.QuietUntil == new TimeOnly(8, 0), "Quiet hours were not durable.");
        Check(reopened.Record("event-1", "project-a", null, null, "Completed", "retry") == null && reopened.History.Count == 3, "Deduplication was not durable.");
        reopened.ConfigureQuietHours(new TimeOnly(0, 0), new TimeOnly(0, 0));
        var quiet = new NotificationCenter(path);
        Check(quiet.IsQuiet(DateTimeOffset.UtcNow), "Equal quiet hour boundaries did not cover the whole day.");
        Check(quiet.Record("event-3", "project-a", null, null, "Waiting", "Quiet event")?.Delivered == false, "Quiet event was delivered.");
        Check(new NotificationCenter(path).History.Count == 4, "Quiet notice was not retained in history.");
        return Task.CompletedTask;
    }
}

