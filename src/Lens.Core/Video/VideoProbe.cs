using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Lens.Core.Video;

public sealed record ProbeResult(int Width, int Height, double Fps, double DurationSeconds);

public static class VideoProbe
{
    public static async Task<ProbeResult> ProbeAsync(string videoPath, string? ffmpegDir = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(FFmpegLocator.FFprobePath(ffmpegDir))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        if (videoPath.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("-rtsp_transport"); psi.ArgumentList.Add("tcp");
        }
        foreach (var a in new[] { "-select_streams", "v:0", "-show_entries",
                     "stream=width,height,r_frame_rate,avg_frame_rate:format=duration", "-of", "json", videoPath })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe failed to start");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0) throw new InvalidOperationException($"ffprobe exited {proc.ExitCode}: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var stream = doc.RootElement.GetProperty("streams")[0];
        var width = stream.GetProperty("width").GetInt32();
        var height = stream.GetProperty("height").GetInt32();
        var fps = ParseRate(stream.TryGetProperty("avg_frame_rate", out var afr) ? afr.GetString() : null);
        if (fps <= 0) fps = ParseRate(stream.TryGetProperty("r_frame_rate", out var rfr) ? rfr.GetString() : null);

        var duration = 0d;
        if (doc.RootElement.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);

        return new ProbeResult(width, height, fps, duration);
    }

    private static double ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate)) return 0;
        var parts = rate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0)
            return n / d;
        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
