using MediaCli.Transcode.Model;
using MediaCli.Transcode.Support;

namespace MediaCli.Transcode.Scan;

/// <summary>
/// Port of ffmpeg_scan.js: 输入文件收集（文件/目录递归）+ 类型过滤 + 规则切片。
/// </summary>
public static class FfmpegScan
{
    /// <summary>收集媒体文件条目（collectInputFiles）：单文件（媒体类型）或目录递归。</summary>
    public static List<ScanEntry> CollectInputFiles(IEnumerable<string> inputs)
    {
        var fileList = new List<ScanEntry>();
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input) || !File.Exists(input) && !Directory.Exists(input)) continue;
            var full = Path.GetFullPath(input);
            if (File.Exists(full))
            {
                if (Helper.IsMediaFile2(full))
                {
                    var fi = new FileInfo(full);
                    fileList.Add(new ScanEntry
                    {
                        Root = Path.GetDirectoryName(full) ?? ".",
                        Path = full,
                        Name = Path.GetFileName(full),
                        Size = fi.Length,
                    });
                }
            }
            else if (Directory.Exists(full))
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                };
                foreach (var file in Directory.EnumerateFiles(full, "*", options))
                {
                    if (!Helper.IsMediaFile2(file)) continue;
                    var fi = new FileInfo(file);
                    fileList.Add(new ScanEntry
                    {
                        Root = full,
                        Path = file,
                        Name = Path.GetFileName(file),
                        Size = fi.Length,
                    });
                }
            }
        }
        // 归一去重（JS path.normalize 语义）
        return Core.UniqueByFields(fileList, e => Path.GetFullPath(e.Path).ToLowerInvariant());
    }

    /// <summary>
    /// 过滤与切片（filterAndSliceEntries）：presetType 类型过滤 → 文件名规则 → start/count。
    /// </summary>
    public static List<ScanEntry> FilterAndSliceEntries(
        IEnumerable<ScanEntry> entries,
        string presetType,
        bool isAudioExtract,
        string? include = null,
        string? exclude = null,
        string? regex = null,
        int start = 0,
        int count = int.MaxValue)
    {
        var filtered = Core.UniqueByFields(entries, e => (object)e.Path).ToList();
        if (presetType == "video" || isAudioExtract)
        {
            filtered = filtered.Where(e => Helper.IsVideoFile(e.Name)).ToList();
        }
        else if (presetType == "audio")
        {
            filtered = filtered.Where(e => Helper.IsAudioFile(e.Name)).ToList();
        }
        return Core.ApplyFileNameRules(filtered, e => e.Name,
            include: include, exclude: exclude, regex: regex, start: start, count: count);
    }
}
