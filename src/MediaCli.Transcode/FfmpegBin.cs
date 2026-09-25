using System.Diagnostics;

namespace MediaCli.Transcode.Bin;

/// <summary>
/// Port of ffmpeg_bin.js: locate ffmpeg/ffprobe executables.
/// Priority: FFMPEG_PATH → FFMPEG_BINARY → PATH (FFPROBE_* for ffprobe,
/// then the selected ffmpeg's sibling directory).
/// A value is only used when the file actually exists.
/// </summary>
public static class FfmpegBin
{
    public static string? ResolveFFmpegBinary(IEnumerable<string>? extraCandidates = null)
    {
        foreach (var key in new[] { "FFMPEG_PATH", "FFMPEG_BINARY" })
        {
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var p = raw.Trim();
            if (File.Exists(p)) return p;
        }
        if (extraCandidates is not null)
        {
            foreach (var candidate in extraCandidates)
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate)) return candidate;
            }
        }
        return Which("ffmpeg");
    }

    public static string? ResolveFFprobeBinary(string? ffmpegPath = null)
    {
        foreach (var key in new[] { "FFPROBE_PATH", "FFPROBE_BINARY" })
        {
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var candidate = raw.Trim();
            if (File.Exists(candidate)) return candidate;
        }
        // 传入的 ffmpeg 同目录优先；否则回退到已解析的 ffmpeg（PATH/环境变量）同目录
        var bin = ffmpegPath ?? ResolveFFmpegBinary();
        if (!string.IsNullOrEmpty(bin))
        {
            var sibling = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(bin)) ?? ".",
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(sibling)) return sibling;
        }
        return Which("ffprobe");
    }

    /// <summary>Minimal `which` over PATH (nothrow semantics: null when missing).</summary>
    public static string? Which(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", ""] : [""];
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var d = dir.Trim().Trim('"');
            if (d.Length == 0) continue;
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(d, name + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>Run a process capturing stdout/stderr; never throws on non-zero exit.</summary>
    public static (int ExitCode, string StdOut, string StdErr) RunCapture(
        string file, IReadOnlyList<string> args, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return (-1, "", "timeout");
        }
        return (proc.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }
}
