using MediaCli.Transcode.Bin;
using Xunit;

namespace MediaCli.Transcode.Tests;

public class FfmpegBinTests
{
    [Fact]
    public void GetBundledCandidates_DiscoversFfmpegInSubdirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ffconv_test_" + Guid.NewGuid().ToString("N"));
        var ffmpegSub = Path.Combine(tempDir, "ffmpeg", "bin");
        Directory.CreateDirectory(ffmpegSub);
        var dummyFfmpeg = Path.Combine(ffmpegSub, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        File.WriteAllText(dummyFfmpeg, "dummy");

        try
        {
            var candidates = FfmpegBin.GetBundledCandidates("ffmpeg", tempDir).ToList();
            Assert.NotEmpty(candidates);
            Assert.Equal(Path.GetFullPath(dummyFfmpeg), Path.GetFullPath(candidates[0]));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ResolveFFmpegBinary_PrefersBundledOverPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ffconv_test_" + Guid.NewGuid().ToString("N"));
        var ffmpegSub = Path.Combine(tempDir, "ffmpeg");
        Directory.CreateDirectory(ffmpegSub);
        var dummyFfmpeg = Path.Combine(ffmpegSub, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
        File.WriteAllText(dummyFfmpeg, "dummy");

        var prevPath = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        var prevBin = Environment.GetEnvironmentVariable("FFMPEG_BINARY");
        try
        {
            Environment.SetEnvironmentVariable("FFMPEG_PATH", null);
            Environment.SetEnvironmentVariable("FFMPEG_BINARY", null);
            var resolved = FfmpegBin.ResolveFFmpegBinary(baseDirectory: tempDir);
            Assert.NotNull(resolved);
            Assert.Equal(Path.GetFullPath(dummyFfmpeg), Path.GetFullPath(resolved!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FFMPEG_PATH", prevPath);
            Environment.SetEnvironmentVariable("FFMPEG_BINARY", prevBin);
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ResolveFFprobeBinary_PrefersBundledFfprobe()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ffconv_test_" + Guid.NewGuid().ToString("N"));
        var ffmpegSub = Path.Combine(tempDir, "ffmpeg");
        Directory.CreateDirectory(ffmpegSub);
        var dummyFfprobe = Path.Combine(ffmpegSub, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        File.WriteAllText(dummyFfprobe, "dummy");

        var prevPath = Environment.GetEnvironmentVariable("FFPROBE_PATH");
        var prevBin = Environment.GetEnvironmentVariable("FFPROBE_BINARY");
        var prevFfmpegPath = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        try
        {
            Environment.SetEnvironmentVariable("FFPROBE_PATH", null);
            Environment.SetEnvironmentVariable("FFPROBE_BINARY", null);
            Environment.SetEnvironmentVariable("FFMPEG_PATH", null);
            var resolved = FfmpegBin.ResolveFFprobeBinary(baseDirectory: tempDir);
            Assert.NotNull(resolved);
            Assert.Equal(Path.GetFullPath(dummyFfprobe), Path.GetFullPath(resolved!));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FFPROBE_PATH", prevPath);
            Environment.SetEnvironmentVariable("FFPROBE_BINARY", prevBin);
            Environment.SetEnvironmentVariable("FFMPEG_PATH", prevFfmpegPath);
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void ResolveMediaInfoBinary_DiscoversBundledMediaInfo()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ffconv_test_" + Guid.NewGuid().ToString("N"));
        var ffmpegSub = Path.Combine(tempDir, "ffmpeg", "bin");
        Directory.CreateDirectory(ffmpegSub);
        var dummyMediainfo = Path.Combine(ffmpegSub, OperatingSystem.IsWindows() ? "mediainfo.exe" : "mediainfo");
        File.WriteAllText(dummyMediainfo, "dummy");

        try
        {
            var resolved = FfmpegBin.ResolveMediaInfoBinary(baseDirectory: tempDir);
            Assert.NotNull(resolved);
            Assert.Equal(Path.GetFullPath(dummyMediainfo), Path.GetFullPath(resolved!));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }
}
