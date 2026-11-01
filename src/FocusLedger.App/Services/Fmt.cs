namespace FocusLedger.App.Services;

internal static class Fmt
{
    public static string Duration(double seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds:F0}s";
        }

        if (seconds < 3600)
        {
            return $"{seconds / 60:F0}m";
        }

        return $"{seconds / 3600:F1}h";
    }

    public static string Clock(long utcMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToLocalTime().ToString("HH:mm");

    public static string ClockWithSeconds(long utcMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToLocalTime().ToString("HH:mm:ss");
}
