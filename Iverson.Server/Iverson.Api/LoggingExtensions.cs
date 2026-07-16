namespace Iverson.Api;

internal static class LoggingExtensions
{
    internal static string SanitizeForLog(this string value) => value.ReplaceLineEndings("");
}
