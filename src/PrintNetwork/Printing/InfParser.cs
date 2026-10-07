using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PrintNetwork.Printing;

/// <summary>Đọc danh sách model máy in khai báo trong file INF của driver (theo kiến trúc máy hiện tại).</summary>
public static partial class InfParser
{
    [GeneratedRegex("%([^%]+)%")]
    private static partial Regex StringToken();

    public static List<string> GetModels(string infPath)
    {
        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        foreach (var raw in File.ReadAllLines(infPath))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                if (!sections.TryGetValue(name, out current)) sections[name] = current = [];
                continue;
            }
            current?.Add(line);
        }

        // [Strings] gốc được ưu tiên, các bản địa hóa [Strings.xxxx] chỉ bổ sung khóa còn thiếu.
        var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in sections.Keys.Where(k => k.StartsWith("Strings", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k.Length))
            foreach (var l in sections[key])
            {
                var (k, v) = SplitKeyValue(l);
                if (k is not null && v is not null) strings.TryAdd(Unquote(k), Unquote(v));
            }

        string Resolve(string s) =>
            StringToken().Replace(Unquote(s), m => strings.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value).Trim();

        var models = new List<string>();
        if (!sections.TryGetValue("Manufacturer", out var manufacturers)) return models;

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => "x86",
        };

        foreach (var l in manufacturers)
        {
            var (_, v) = SplitKeyValue(l);
            if (string.IsNullOrWhiteSpace(v)) continue;
            var parts = v.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            if (parts.Length == 0) continue;
            var baseName = parts[0];
            var decorated = parts.Skip(1)
                .Where(d => d.Contains(arch, StringComparison.OrdinalIgnoreCase))
                .Select(d => baseName + "." + d)
                .ToList();
            var targets = decorated.Count > 0 ? decorated : [baseName];

            foreach (var section in targets)
            {
                if (!sections.TryGetValue(section, out var lines)) continue;
                foreach (var m in lines)
                {
                    var (k, _) = SplitKeyValue(m);
                    if (k is null) continue;
                    var model = Resolve(k);
                    if (model.Length > 0 && !models.Contains(model, StringComparer.OrdinalIgnoreCase)) models.Add(model);
                }
            }
        }
        return models;
    }

    static string StripComment(string line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            else if (line[i] == ';' && !inQuote) return line[..i];
        }
        return line;
    }

    /// <summary>Tách "key = value" tại dấu '=' đầu tiên nằm ngoài nháy kép. Không có '=' thì key = null.</summary>
    static (string? Key, string? Value) SplitKeyValue(string line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            else if (line[i] == '=' && !inQuote) return (line[..i].Trim(), line[(i + 1)..].Trim());
        }
        return (null, line.Trim());
    }

    static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Replace("\"\"", "\"");
        return s;
    }
}
