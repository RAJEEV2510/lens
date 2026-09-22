using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Lens.Core.Inference;

namespace Lens.Core.Video;

/// <summary>A sampled frame, already letterboxed to the model input size as packed RGB24 bytes.</summary>
public sealed record Frame(byte[] Rgb, int Index, double TimestampSeconds);

/// <summary>
/// Streams frames out of ffmpeg at a fixed sample rate. ffmpeg does the decode, the fps drop,
/// the aspect-preserving resize and the padding, so .NET only ever sees fixed-size RGB buffers.
/// </summary>
public sealed class FrameSampler
{
    private readonly string _ffmpeg;

    public FrameSampler(string? ffmpegDir = null) => _ffmpeg = FFmpegLocator.FFmpegPath(ffmpegDir);

    public static bool IsStreamUrl(string path) =>
        path.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("udp://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("srt://", StringComparison.OrdinalIgnoreCase);

    /// <summary>ffmpeg input flags for a source: TCP transport and low-latency flags for streams, real-time pacing when simulating a camera from a file.</summary>
    public static IEnumerable<string> InputArgs(string path, bool simulateRealtime = false)
    {
        if (path.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            yield return "-rtsp_transport"; yield return "tcp";
        }
        if (IsStreamUrl(path))
        {
            yield return "-fflags"; yield return "nobuffer";
            yield return "-flags"; yield return "low_delay";
        }
        else if (simulateRealtime)
        {
            yield return "-re";
        }
    }

    public async IAsyncEnumerable<Frame> SampleAsync(
        string videoPath,
        Letterbox letterbox,
        double sampleFps,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var f in SampleAsync(videoPath, letterbox, sampleFps, simulateRealtime: false, ct))
            yield return f;
    }

    public async IAsyncEnumerable<Frame> SampleAsync(
        string videoPath,
        Letterbox letterbox,
        double sampleFps,
        bool simulateRealtime,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var inv = CultureInfo.InvariantCulture;
        var size = letterbox.TargetSize;
        // round=up matters: the fps filter emits the LAST source frame whose rounded timestamp lands in a slot.
        // With the default "near", slot t gets the frame at t + 0.24s (for 25 fps input); with "up" it gets the
        // frame at exactly t, which is also what a later "-ss t" seek returns, so boxes line up with grabbed frames.
        var vf = string.Format(inv,
            "fps={0}:round=up,scale={1}:{2}:flags=bilinear,pad={3}:{3}:{4}:{5}:color=#727272",
            sampleFps, letterbox.ScaledWidth, letterbox.ScaledHeight, size, letterbox.PadX, letterbox.PadY);

        var psi = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        foreach (var a in InputArgs(videoPath, simulateRealtime)) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-i", videoPath, "-vf", vf, "-f", "rawvideo", "-pix_fmt", "rgb24", "-" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed to start");
        // A pipe read cannot be cancelled on its own, so cancellation kills ffmpeg, which closes the pipe. Also guards against
        // orphaned ffmpeg processes when the consumer stops early (stream stall, source removed, shutdown).
        using var killOnCancel = ct.Register(() => TryKill(proc));
        try
        {
            var stderrTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            var stdout = proc.StandardOutput.BaseStream;

            var frameBytes = size * size * 3;
            var index = 0;
            while (!ct.IsCancellationRequested)
            {
                var buffer = new byte[frameBytes];
                var read = 0;
                while (read < frameBytes)
                {
                    var n = await stdout.ReadAsync(buffer.AsMemory(read, frameBytes - read), ct);
                    if (n == 0) break;
                    read += n;
                }
                if (read < frameBytes) break;

                yield return new Frame(buffer, index, index / sampleFps);
                index++;
            }

            ct.ThrowIfCancellationRequested();
            await proc.WaitForExitAsync(CancellationToken.None);
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}: {stderr}");
        }
        finally
        {
            TryKill(proc);
        }
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>
    /// Streams a source as multipart MJPEG into an output stream until cancelled. ffmpeg's mpjpeg muxer writes the
    /// "--ffmpeg" boundaries itself, so the HTTP response only needs the matching content type.
    /// </summary>
    public async Task StreamMjpegAsync(string source, Stream output, double fps, int maxWidth, bool simulateRealtime, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        foreach (var a in InputArgs(source, simulateRealtime)) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "-i", source, "-an",
                     "-vf", string.Format(inv, "fps={0},scale='min({1},iw)':-2", fps, maxWidth),
                     "-q:v", "6", "-f", "mpjpeg", "-" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed to start");
        using var killOnCancel = ct.Register(() => TryKill(proc));
        try
        {
            _ = proc.StandardError.ReadToEndAsync(CancellationToken.None);
            var buffer = new byte[64 * 1024];
            var stdout = proc.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                var n = await stdout.ReadAsync(buffer, ct);
                if (n == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                await output.FlushAsync(ct);
            }
        }
        finally
        {
            TryKill(proc);
        }
    }

    /// <summary>Grab a single JPEG at a timestamp, used by the API to show a matched frame.</summary>
    public async Task<byte[]> GrabJpegAsync(string videoPath, double timestampSeconds, int maxWidth = 960, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var scale = "scale=" + maxWidth + ":-2";
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error",
                     "-ss", timestampSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", videoPath,
                     "-frames:v", "1", "-vf", scale, "-f", "image2", "-vcodec", "mjpeg", "-q:v", "3", "-" })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed to start");
        using var ms = new MemoryStream();
        await proc.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}: {stderr}");
        return ms.ToArray();
    }
}
