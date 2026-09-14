namespace NetTrace.App.Services;

public static class ByteFormat
{
    // Binary units: a 5 GB budget must read as 5 GB on screen, not 4.7.
    public const double Kilobyte = 1024;
    public const double Megabyte = Kilobyte * 1024;
    public const double Gigabyte = Megabyte * 1024;

    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Size(double bytes)
    {
        if (bytes < 1) return "0 B";

        int unit = 0;
        while (bytes >= Kilobyte && unit < Units.Length - 1)
        {
            bytes /= Kilobyte;
            unit++;
        }

        // Whole numbers for bytes and kilobytes, one decimal above that: a download reading
        // "1.4 GB" is useful, "1.4 KB" is noise.
        return unit <= 1
            ? $"{System.Math.Round(bytes)} {Units[unit]}"
            : $"{bytes:0.#} {Units[unit]}";
    }

    public static string Rate(double bytesPerSecond) => $"{Size(bytesPerSecond)}/s";
}
