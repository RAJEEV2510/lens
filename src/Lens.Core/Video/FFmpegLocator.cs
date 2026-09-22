namespace Lens.Core.Video;

/// <summary>Finds ffmpeg/ffprobe: explicit dir, LENS_FFMPEG_DIR, a tools/ffmpeg/bin folder above the app, then PATH.</summary>
public static class FFmpegLocator
{
    public static string FFmpegPath(string? configuredDir = null) => Find("ffmpeg", configuredDir);
    public static string FFprobePath(string? configuredDir = null) => Find("ffprobe", configuredDir);

    private static string Find(string tool, string? configuredDir)
    {
        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;

        foreach (var dir in CandidateDirs(configuredDir))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }

        // Fall back to PATH resolution by the OS.
        return exe;
    }

    private static IEnumerable<string> CandidateDirs(string? configuredDir)
    {
        if (!string.IsNullOrWhiteSpace(configuredDir)) yield return configuredDir;

        var env = Environment.GetEnvironmentVariable("LENS_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(env)) yield return env;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "tools", "ffmpeg", "bin");
        }
    }
}
