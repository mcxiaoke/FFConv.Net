using MediaCli.Transcode.Build;
using MediaCli.Transcode.Hardware;
using MediaCli.Transcode.Model;
using MediaCli.Transcode.Planning;
using MediaCli.Transcode.Presets;
using MediaCli.Transcode.Run;
using MediaCli.Transcode.Scan;
using MediaCli.Transcode.Support;

namespace MediaCli.Transcode.Cli;

/// <summary>
/// mediac-dotnet CLI：presets / plan / run 三个子命令。
/// 与 Node 版 cmd_ffmpeg 的契约一致：默认 dry-run，--doit 才真正执行。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
            {
                PrintHelp();
                return 0;
            }
            if (args[0] is "--version" or "-v" or "version")
            {
                Console.WriteLine($"mediac-dotnet {BuildInfo.DisplayString}");
                return 0;
            }
            var command = args[0].ToLowerInvariant();
            var rest = args[1..];
            return command switch
            {
                "presets" or "ps" => RunPresets(),
                "plan" => RunPlan(rest),
                "run" => RunPlan(rest),
                _ => Unknown(command),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"error: {e.Message}");
            return 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            mediac-dotnet v{BuildInfo.AppVersion} — media-cli.js src/transcode 的 C#/.NET 移植版

            用法:
              mediac-dotnet presets                                    列出全部预设
              mediac-dotnet plan   --input <path> [options]            dry-run（只打印计划与命令）
              mediac-dotnet run    --input <path> [options] [--doit]   执行转码（默认 dry-run）

            选项:
              --input <path>        输入文件或目录（可重复）
              --preset <name>       预设名（默认 hevc_2k；anime/hevc/h264/av1 等别名同 JS）
              --output <dir>        输出目录
              --output-mode <mode>  tree | dir | file（默认 dir）
              --hwaccel <name>      auto|cuda|qsv|amf|d3d|d3d11va|d3d12va|dxva2|cpu
              --decode-mode <mode>  auto | gpu | cpu
              --ffargs "k=v,..."    复合参数（vb/vq/ab/aq/dm/sp/fps/vc/ac/md...）
              --video-copy          视频流复制
              --audio-copy          音频流复制
              --strict              严格模式（不降级）
              --override            覆盖已有目标
              --debug               ffmpeg 输出详细日志
              --anime               动漫模式
              --include/--exclude/--regex/--start/--count   文件过滤与切片
              --doit                真正执行（仅 run）
            """);
    }

    private sealed class CliArgs
    {
        public List<string> Inputs { get; } = [];
        public string? Preset;
        public string Output = "";
        public string OutputMode = "dir";
        public string? Hwaccel;
        public string DecodeMode = "auto";
        public string? Ffargs;
        public bool VideoCopy;
        public bool AudioCopy;
        public bool Strict;
        public bool Override;
        public bool Debug;
        public bool Anime;
        public bool DoIt;
        public string? Include;
        public string? Exclude;
        public string? Regex;
        public int Start;
        public int Count = int.MaxValue;
        public bool ErrorFile;
    }

    private static CliArgs ParseArgs(string[] args)
    {
        var cli = new CliArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"missing value for {a}");
            switch (a)
            {
                case "--input" or "-i":
                    cli.Inputs.Add(Next());
                    break;
                case "--preset" or "-p":
                    cli.Preset = Next();
                    break;
                case "--output" or "-o":
                    cli.Output = Next();
                    break;
                case "--output-mode":
                    cli.OutputMode = Next();
                    break;
                case "--hwaccel":
                    cli.Hwaccel = Next();
                    break;
                case "--decode-mode":
                    cli.DecodeMode = Next();
                    break;
                case "--ffargs":
                    cli.Ffargs = Next();
                    break;
                case "--video-copy":
                    cli.VideoCopy = true;
                    break;
                case "--audio-copy":
                    cli.AudioCopy = true;
                    break;
                case "--strict":
                    cli.Strict = true;
                    break;
                case "--override":
                    cli.Override = true;
                    break;
                case "--debug":
                    cli.Debug = true;
                    break;
                case "--anime":
                    cli.Anime = true;
                    break;
                case "--doit":
                    cli.DoIt = true;
                    break;
                case "--error-file":
                    cli.ErrorFile = true;
                    break;
                case "--include":
                    cli.Include = Next();
                    break;
                case "--exclude":
                    cli.Exclude = Next();
                    break;
                case "--regex":
                    cli.Regex = Next();
                    break;
                case "--start":
                    cli.Start = int.Parse(Next());
                    break;
                case "--count":
                    cli.Count = int.Parse(Next());
                    break;
                default:
                    throw new ArgumentException($"unknown option: {a}");
            }
        }
        if (cli.Inputs.Count == 0) throw new ArgumentException("--input is required");
        return cli;
    }

    private static ArgvOptions BuildArgv(CliArgs cli) => new()
    {
        DecodeMode = cli.DecodeMode,
        Hwaccel = cli.Hwaccel,
        Strict = cli.Strict,
        Override = cli.Override,
        Debug = cli.Debug,
        Anime = cli.Anime,
        Output = cli.Output,
        OutputMode = cli.OutputMode,
        ErrorFile = cli.ErrorFile ? "txt" : null,
    };

    private static FFmpegPresets.ArgvShim BuildShim(CliArgs cli)
    {
        var shim = new FFmpegPresets.ArgvShim
        {
            Preset = cli.Preset,
            VideoCopy = cli.VideoCopy,
            AudioCopy = cli.AudioCopy,
            Anime = cli.Anime,
        };
        if (cli.Ffargs is not null)
        {
            FFmpegPresets.ApplyFfargs(shim, FFmpegPresets.ParseFfargs(cli.Ffargs));
        }
        return shim;
    }

    private static int RunPresets()
    {
        FFmpegPresets.Init();
        foreach (var name in FFmpegPresets.GetAllNames())
        {
            var p = FFmpegPresets.GetPreset(name)!;
            Console.WriteLine(
                $"{name,-12} {p.Type,-5} {p.Format,-6} q={HwAccelInternal.FormatNum(p.VideoQuality),-4} " +
                $"dim={p.Dimension,-5} ab={p.AudioBitrate / 1000}k family={p.VideoCodecFamily ?? "-"}");
        }
        return 0;
    }

    private static int RunPlan(string[] args)
    {
        var cli = ParseArgs(args);
        FFmpegPresets.Init();
        var argv = BuildArgv(cli);
        var shim = BuildShim(cli);
        var preset = FFmpegPresets.CreateFromArgv(shim);

        var entries = FfmpegScan.CollectInputFiles(cli.Inputs);
        var filtered = FfmpegScan.FilterAndSliceEntries(
            entries, preset.Type ?? "video", preset.Name == "audio_extract",
            include: cli.Include, exclude: cli.Exclude, regex: cli.Regex,
            start: cli.Start, count: cli.Count);
        if (filtered.Count == 0)
        {
            Console.WriteLine("no media files found");
            return 1;
        }

        var deps = new TaskDeps { Output = cli.Output, OutputMode = cli.OutputMode };
        var ok = 0;
        var skipped = 0;
        var tasks = new List<TranscodeEntry>();
        for (var i = 0; i < filtered.Count; i++)
        {
            var e = filtered[i];
            var entry = new TranscodeEntry
            {
                Index = i,
                Total = filtered.Count,
                Root = e.Root,
                Path = e.Path,
                Name = e.Name,
                Size = e.Size,
                Preset = preset.Clone(),
                Argv = argv,
                TestMode = !cli.DoIt,
                StartMs = Environment.TickCount64,
            };
            var task = FfmpegTask.BuildCliTask(entry, deps);
            tasks.Add(task);
            if (task.Skipped)
            {
                skipped++;
                Console.WriteLine($"skip[{task.SkipReason}] {task.Path}");
                continue;
            }
            ok++;
            var dst = task.DstArgs!;
            Console.WriteLine(
                $"#{i + 1}/{filtered.Count} {Helper.PathShort(task.Path, 64)}\n" +
                $"  dst: {task.FileDst}\n" +
                $"  info: src={dst.SrcWidth}x{dst.SrcHeight} -> {dst.DstWidth}x{dst.DstHeight}, " +
                $"vq={HwAccelInternal.FormatNum(dst.DstVideoQuality)} vb={dst.DstVideoBitrate / 1000}k ab={dst.DstAudioBitrate / 1000}k");
            // dry-run：构建完整命令并打印（不探测硬件层，plan 阶段不跑探测）
            if (!cli.DoIt)
            {
                var cpuPlan = new HwPlan
                {
                    Tier = Array.Find(HwAccel.Tiers, t => t.Name == "cpu")!,
                    Size = null,
                    Reason = "dry-run preview (cpu tier placeholder)",
                };
                var preview = FfmpegBuild.CreateFFmpegArgs(task, cpuPlan);
                Console.WriteLine($"  cmd: ffmpeg {FfmpegBuild.FlattenFFArgs(preview.Args)}");
            }
        }

        if (!cli.DoIt)
        {
            Console.WriteLine($"\n[dry-run] {ok} task(s) would run, {skipped} skipped. Use --doit to execute.");
            return 0;
        }

        // 真实执行
        var success = 0;
        var failed = 0;
        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            if (task.Skipped || task.DstExists) continue;
            Console.WriteLine($"\n=== ({i + 1}/{tasks.Count}) {task.Path}");
            task.TestMode = false;
            var result = FfmpegRun.RunFFmpeg(task).ToRunResult();
            switch (result.Status)
            {
                case RunStatus.Success:
                    success++;
                    Console.WriteLine($"  done: {result.OutputPath}");
                    break;
                case RunStatus.Skipped:
                    skipped++;
                    Console.WriteLine($"  skipped: {result.Reason}");
                    break;
                case RunStatus.Cancelled:
                    Console.WriteLine("  cancelled");
                    return 130;
                default:
                    failed++;
                    Console.Error.WriteLine($"  failed[{result.Stage}]: {result.Error}");
                    break;
            }
        }
        Console.WriteLine($"\nsummary: success={success} failed={failed} skipped={skipped}");
        return failed > 0 ? 2 : 0;
    }
}
