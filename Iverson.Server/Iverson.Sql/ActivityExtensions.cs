using System.Diagnostics;

namespace Iverson.Sql;

internal static class ActivityExtensions
{
    internal static void RecordException(this Activity? activity, Exception ex)
    {
        if (activity is null) return;
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = ex.GetType().FullName,
            ["exception.message"] = ex.Message,
            ["exception.stacktrace"] = ex.ToString()
        }));
    }
}
