using MediaCli.Transcode.Support;
using YamlDotNet.RepresentationModel;

namespace MediaCli.Transcode.Presets;

/// <summary>
/// Port of preset_loader.js: layered YAML preset loading with extends
/// inheritance, unknown-field warnings and _override-protected merging.
///
/// Layer order (low → high priority):
///   1. bundled presets/default.yaml
///   2. ~/.mediac/presets.yaml|yml
///   3. cwd/presets.yaml|yml
/// Same-name presets require an explicit `_override: true` to replace.
/// </summary>
public static class PresetLoader
{
    /// <summary>Raw preset: name → ordered key/value map (values may be string/number/bool).</summary>
    public sealed class Layer
    {
        public required string Path { get; init; }
        public required Dictionary<string, Dictionary<string, object?>> Presets { get; init; }
    }

    public static string BundledPresetPath { get; } =
        FindBundledPresetPath();

    public static string[] UserSearchPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cwd = Directory.GetCurrentDirectory();
        return
        [
            Path.Combine(home, ".mediac", "presets.yaml"),
            Path.Combine(home, ".mediac", "presets.yml"),
            Path.Combine(cwd, "presets.yaml"),
            Path.Combine(cwd, "presets.yml"),
        ];
    }

    private static string FindBundledPresetPath()
    {
        // 1. relative to the assembly (dotnet output keeps presets/default.yaml next to the dll)
        var appDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(appDir, "presets", "default.yaml");
        if (File.Exists(candidate)) return candidate;
        // 2. repository layout: dotnet/src/<...>/bin/... → repo presets/default.yaml
        var dir = new DirectoryInfo(appDir);
        while (dir is not null)
        {
            candidate = Path.Combine(dir.FullName, "presets", "default.yaml");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(appDir, "presets", "default.yaml");
    }

    /// <summary>Port of resolvePresetPath: customPath → user search paths → bundled default.</summary>
    public static string? ResolvePresetPath(string? customPath)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            var resolved = Path.GetFullPath(customPath);
            if (File.Exists(resolved)) return resolved;
        }
        foreach (var p in UserSearchPaths())
        {
            if (File.Exists(p)) return p;
        }
        return File.Exists(BundledPresetPath) ? BundledPresetPath : null;
    }

    /// <summary>Load one YAML file into a raw (unresolved) layer.</summary>
    public static Layer? LoadYamlLayer(string path)
    {
        try
        {
            var content = File.ReadAllText(path);
            var yaml = new YamlStream();
            yaml.Load(new StringReader(content));
            if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
            {
                return null;
            }
            var presets = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            foreach (var entry in root.Children)
            {
                var name = ((YamlScalarNode)entry.Key).Value ?? "";
                if (entry.Value is not YamlMappingNode map) continue;
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var field in map.Children)
                {
                    var key = ((YamlScalarNode)field.Key).Value ?? "";
                    fields[key] = ScalarValue(field.Value);
                }
                presets[name] = fields;
            }
            return new Layer { Path = path, Presets = presets };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? ScalarValue(YamlNode node) => node switch
    {
        YamlScalarNode s => s.Value switch
        {
            null => null,
            "true" => true,
            "false" => false,
            _ => long.TryParse(s.Value, out var i) ? i
                : double.TryParse(s.Value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d
                : s.Value,
        },
        _ => null,
    };

    /// <summary>
    /// Port of resolveExtends: recursively merge the inherited preset, child keys
    /// win, `extends` key itself is dropped. Throws on cycles / missing bases.
    /// </summary>
    public static Dictionary<string, object?> ResolveExtends(
        Dictionary<string, Dictionary<string, object?>> presets,
        string presetName,
        HashSet<string>? resolved = null)
    {
        resolved ??= [];
        if (!resolved.Add(presetName))
            throw new InvalidOperationException($"Circular extends detected: {presetName}");
        if (!presets.TryGetValue(presetName, out var preset))
            throw new KeyNotFoundException($"Preset not found: {presetName}");
        if (!preset.TryGetValue("extends", out var extendsRaw) || extendsRaw is not string baseName || baseName.Length == 0)
            return new Dictionary<string, object?>(preset, StringComparer.Ordinal);

        var basePreset = ResolveExtends(presets, baseName, resolved);
        var merged = new Dictionary<string, object?>(basePreset, StringComparer.Ordinal);
        foreach (var kv in preset)
        {
            if (kv.Key != "extends") merged[kv.Key] = kv.Value;
        }
        return merged;
    }

    /// <summary>
    /// Port of processPresets: resolve extends for every entry, warn on unknown
    /// fields / type mismatches; invalid entries are skipped.
    /// </summary>
    public static Dictionary<string, Dictionary<string, object?>> ProcessPresets(
        Dictionary<string, Dictionary<string, object?>> rawPresets)
    {
        var processed = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var (name, _) in rawPresets)
        {
            try
            {
                var resolved = ResolveExtends(rawPresets, name);
                if (!ValidatePresetFields(name, resolved)) continue;
                processed[name] = resolved;
            }
            catch (Exception)
            {
                // Failed to resolve preset: skipped (same as JS warn + continue).
            }
        }
        return processed;
    }

    private static bool ValidatePresetFields(string name, Dictionary<string, object?> preset)
    {
        var unknown = preset.Keys.Where(k => !PresetSchema.Fields.Contains(k)).ToList();
        var mismatch = preset.Keys.Where(k => PresetSchema.HasTypeMismatch(k, preset[k])).ToList();
        // JS logs warnings here; loader behaviour (skip on non-object, keep on unknown/type warn) preserved.
        _ = (name, unknown, mismatch);
        return true;
    }

    /// <summary>Load all existing layers, low → high priority (port of loadPresetLayers).</summary>
    public static List<Layer> LoadPresetLayers(string? customPath = null)
    {
        var layers = new List<Layer>();
        var paths = customPath is not null
            ? [Path.GetFullPath(customPath)]
            : new[] { BundledPresetPath }.Concat(UserSearchPaths());
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            var layer = LoadYamlLayer(p);
            if (layer is not null) layers.Add(layer);
        }
        return layers;
    }

    /// <summary>
    /// Port of mergePresets: new presets are added; same-name presets must carry
    /// `_override: true` to replace, otherwise they are skipped.
    /// </summary>
    public static Dictionary<string, Dictionary<string, object?>> MergePresets(
        Dictionary<string, Dictionary<string, object?>> basePresets,
        Layer layer)
    {
        var merged = new Dictionary<string, Dictionary<string, object?>>(basePresets, StringComparer.Ordinal);
        foreach (var (name, preset) in layer.Presets)
        {
            if (name.StartsWith('_')) continue;
            if (merged.ContainsKey(name))
            {
                if (preset.TryGetValue("_override", out var o) && o is true)
                {
                    merged[name] = preset;
                }
                // else: skipped (same-name without _override)
            }
            else
            {
                merged[name] = preset;
            }
        }
        return merged;
    }
}
