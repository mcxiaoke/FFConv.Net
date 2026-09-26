using System.Reflection;

namespace MediaCli.Transcode.Support;

/// <summary>
/// 构建信息与版本元数据服务。
///
/// 构建元数据由 Directory.Build.props 的 GenerateCustomVersion 目标注入（AssemblyMetadata）：
/// GitCommit（git short hash）/ BuildTime（yyyy-MM-dd HH:mm:ss）/ DaysSinceEpoch。
/// </summary>
public static class BuildInfo
{
    private static Assembly TargetAssembly => typeof(BuildInfo).Assembly;

    /// <summary>语义化版本号（Major.Minor.Patch）</summary>
    public static string AppVersion
    {
        get
        {
            try
            {
                var ver = TargetAssembly.GetName().Version;
                return ver != null ? $"{ver.Major}.{ver.Minor}.{ver.Build}" : "1.0.0";
            }
            catch
            {
                return "1.0.0";
            }
        }
    }

    /// <summary>完整产品版本（含前缀与后缀，如 1.0.0-debug 或 1.0.0-20260925-d1ea0eb）</summary>
    public static string InformationalVersion
    {
        get
        {
            try
            {
                var attr = TargetAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                return attr?.InformationalVersion ?? AppVersion;
            }
            catch
            {
                return AppVersion;
            }
        }
    }

    /// <summary>构建日期时间（AssemblyMetadata.BuildTime，Directory.Build.props 注入）</summary>
    public static string BuildTime => GetMetadata("BuildTime");

    /// <summary>Git 短提交号（AssemblyMetadata.GitCommit，Directory.Build.props 注入）</summary>
    public static string GitCommit => GetMetadata("GitCommit");

    /// <summary>基准日起算天数（AssemblyMetadata.DaysSinceEpoch，Directory.Build.props 注入）</summary>
    public static string DaysSinceEpoch => GetMetadata("DaysSinceEpoch");

    /// <summary>格式化展示串：v1.0.0 (2026-09-25 22:00:00, d1ea0eb)</summary>
    public static string DisplayString
    {
        get
        {
            var time = BuildTime;
            var commit = GitCommit;
            var details = new List<string>();
            if (!string.IsNullOrEmpty(time)) details.Add(time);
            if (!string.IsNullOrEmpty(commit)) details.Add(commit);

            return details.Count > 0
                ? $"v{AppVersion} ({string.Join(", ", details)})"
                : $"v{AppVersion}";
        }
    }

    /// <summary>读取指定程序集的 AssemblyMetadata 属性值</summary>
    private static string GetMetadata(string key)
    {
        try
        {
            var attr = TargetAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));
            return attr?.Value ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
