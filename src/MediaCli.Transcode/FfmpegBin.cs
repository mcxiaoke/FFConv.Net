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
    /// <summary>
    /// 获取 exe 同目录及 ffmpeg/ 等子目录下的预置二进制候选路径（优先于系统 PATH）。
    /// 支持查找的子目录：&lt;baseDir&gt;/ffmpeg/bin、&lt;baseDir&gt;/ffmpeg、&lt;baseDir&gt;/bin、&lt;baseDir&gt;/。
    /// </summary>
    public static IEnumerable<string> GetBundledCandidates(string name, string? baseDirectory = null)
    {
        var baseDir = !string.IsNullOrEmpty(baseDirectory) ? baseDirectory : AppContext.BaseDirectory;
        if (string.IsNullOrEmpty(baseDir)) yield break;

        var fileNames = OperatingSystem.IsWindows()
            ? (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? new[] { name } : new[] { $"{name}.exe", name })
            : new[] { name };

        string[] subDirs = [
            Path.Combine(baseDir, "ffmpeg", "bin"),
            Path.Combine(baseDir, "ffmpeg"),
            Path.Combine(baseDir, "bin"),
            baseDir
        ];

        foreach (var dir in subDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var fn in fileNames)
            {
                var full = Path.Combine(dir, fn);
                if (File.Exists(full))
                {
                    yield return full;
                }
            }
        }
    }

    public static string? ResolveFFmpegBinary(IEnumerable<string>? extraCandidates = null, string? baseDirectory = null)
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
        foreach (var candidate in GetBundledCandidates("ffmpeg", baseDirectory))
        {
            return candidate;
        }
        return Which("ffmpeg");
    }

    public static string? ResolveFFprobeBinary(string? ffmpegPath = null, string? baseDirectory = null)
    {
        foreach (var key in new[] { "FFPROBE_PATH", "FFPROBE_BINARY" })
        {
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var candidate = raw.Trim();
            if (File.Exists(candidate)) return candidate;
        }

        // 显式传入的 ffmpeg 优先看其同级目录
        if (!string.IsNullOrEmpty(ffmpegPath))
        {
            var sibling = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(ffmpegPath)) ?? ".",
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(sibling)) return sibling;
        }

        // 本地随附候选（exe 同级 / ffmpeg/ 等子目录）优先于系统 PATH
        foreach (var candidate in GetBundledCandidates("ffprobe", baseDirectory))
        {
            return candidate;
        }

        // 若上述均未命中，检查已解析的 ffmpeg 同级目录（若有）
        var bin = ffmpegPath ?? ResolveFFmpegBinary(baseDirectory: baseDirectory);
        if (!string.IsNullOrEmpty(bin))
        {
            var sibling = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(bin)) ?? ".",
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(sibling)) return sibling;
        }

        return Which("ffprobe");
    }

    public static string? ResolveMediaInfoBinary(string? ffmpegPath = null, string? baseDirectory = null)
    {
        foreach (var key in new[] { "MEDIAINFO_PATH", "MEDIAINFO_BINARY" })
        {
            var raw = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var candidate = raw.Trim();
            if (File.Exists(candidate)) return candidate;
        }

        // 显式传入的 ffmpeg 优先看其同级目录
        if (!string.IsNullOrEmpty(ffmpegPath))
        {
            var sibling = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(ffmpegPath)) ?? ".",
                OperatingSystem.IsWindows() ? "mediainfo.exe" : "mediainfo");
            if (File.Exists(sibling)) return sibling;
        }

        // 本地随附候选（exe 同级 / ffmpeg/ 等子目录）优先于系统 PATH
        foreach (var candidate in GetBundledCandidates("mediainfo", baseDirectory))
        {
            return candidate;
        }

        var bin = ffmpegPath ?? ResolveFFmpegBinary(baseDirectory: baseDirectory);
        if (!string.IsNullOrEmpty(bin))
        {
            var sibling = Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(bin)) ?? ".",
                OperatingSystem.IsWindows() ? "mediainfo.exe" : "mediainfo");
            if (File.Exists(sibling)) return sibling;
        }

        return Which("mediainfo");
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
