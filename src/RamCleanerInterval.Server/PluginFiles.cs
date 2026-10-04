using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace RamCleanerInterval.Server;

/// <summary>
/// The RAM cleaner's files on this PC, found by walking up from this DLL (SPT_Runtime\user\mods\...) to the folder that
/// holds BepInEx: its config (.cfg: port, on/off, language and every setting), the settings list the game writes at
/// start (settings-catalog.json: names/descriptions in both languages, types, ranges) and the session reports.
/// </summary>
internal sealed class PluginFiles
{
    private const string ConfigName = "com.cactuspie.ramcleanerinterval.cfg";
    private static readonly object WriteGate = new();

    public string? ConfigPath { get; private set; }
    public string? DataDir { get; private set; }

    public int Port { get; private set; } = 6977;
    public bool Enabled { get; private set; } = true;
    public bool English { get; private set; }

    /// <summary>"section|key" → value as written in the .cfg.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public static PluginFiles Read()
    {
        var files = new PluginFiles();
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        for (var i = 0; i < 6 && dir is not null; i++, dir = Path.GetDirectoryName(dir))
        {
            var bepinex = Path.Combine(dir, "BepInEx");
            if (!Directory.Exists(bepinex))
            {
                continue;
            }

            var config = Path.Combine(bepinex, "config", ConfigName);
            files.ConfigPath = File.Exists(config) ? config : null;
            var data = Path.Combine(bepinex, "RamCleaner");
            files.DataDir = Directory.Exists(data) ? data : null;
            break;
        }

        if (files.ConfigPath is not null)
        {
            try
            {
                files.Parse(File.ReadAllLines(files.ConfigPath));
            }
            catch (IOException)
            {
                // being written by the game right now: defaults for this request
            }
        }

        return files;
    }

    private void Parse(string[] lines)
    {
        var section = string.Empty;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            var eq = line.IndexOf(" =", StringComparison.Ordinal); // "key = value", or "key =" when empty
            if (eq > 0)
            {
                Values[section + "|" + line[..eq]] = line[(eq + 2)..].Trim();
            }
        }

        if (Values.TryGetValue("18. Web page|Port", out var port) && int.TryParse(port, out var p) && p is > 0 and < 65536)
        {
            Port = p;
        }

        Enabled = !(Values.TryGetValue("18. Web page|Enabled", out var enabled) && enabled.Equals("false", StringComparison.OrdinalIgnoreCase));
        English = Values.TryGetValue("0. Mode|Language", out var language) && language == "English";
    }

    /// <summary>The settings list the game wrote at its last start, or null (game never started with 2.12+).</summary>
    public JsonElement? Catalog()
    {
        var path = DataDir is null ? null : Path.Combine(DataDir, "settings-catalog.json");
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks <paramref name="value"/> against the entry's type/range/choices (clamping numbers like BepInEx does) and
    /// writes it into the .cfg line "key = value" of its section. Returns the stored value, or throws FormatException.
    /// </summary>
    public string Write(JsonElement entry, string value)
    {
        if (ConfigPath is null)
        {
            throw new FormatException("config file not found");
        }

        var section = entry.GetProperty("section").GetString()!;
        var key = entry.GetProperty("key").GetString()!;
        var stored = Normalize(entry, value);

        lock (WriteGate)
        {
            var lines = File.ReadAllLines(ConfigPath).ToList();
            var current = string.Empty;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i].Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    current = line[1..^1];
                }
                else if (current == section && (line == key + " =" || line.StartsWith(key + " = ", StringComparison.Ordinal)))
                {
                    lines[i] = key + " = " + stored;
                    File.WriteAllLines(ConfigPath, lines, new UTF8Encoding(false)); // same as BepInEx: UTF-8 without BOM
                    Values[section + "|" + key] = stored;
                    return stored;
                }
            }
        }

        throw new FormatException("setting not found in the config file (start the game once)");
    }

    private static string Normalize(JsonElement entry, string value)
    {
        var kind = entry.GetProperty("kind").GetString();
        double? min = entry.TryGetProperty("min", out var mn) ? mn.GetDouble() : null;
        double? max = entry.TryGetProperty("max", out var mx) ? mx.GetDouble() : null;
        switch (kind)
        {
            case "bool":
                return bool.TryParse(value, out var b) ? (b ? "true" : "false") : throw new FormatException("expected true/false");
            case "int":
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                {
                    throw new FormatException("expected a whole number");
                }

                return ((int)Math.Clamp(i, min ?? int.MinValue, max ?? int.MaxValue)).ToString(CultureInfo.InvariantCulture);
            case "float":
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || double.IsNaN(f) || double.IsInfinity(f))
                {
                    throw new FormatException("expected a number");
                }

                return ((float)Math.Clamp(f, min ?? double.MinValue, max ?? double.MaxValue)).ToString(CultureInfo.InvariantCulture);
            case "list":
                return entry.GetProperty("options").EnumerateArray().Select(o => o.GetString()).FirstOrDefault(o => o == value)
                       ?? throw new FormatException("not one of the choices");
            default:
                return value.Contains('\n') || value.Contains('\r') ? throw new FormatException("one line only") : value.Trim();
        }
    }

    /// <summary>Newest first, at most <paramref name="count"/>.</summary>
    public FileInfo[] Reports(int count) => DataDir is null
        ? []
        : new DirectoryInfo(DataDir).GetFiles("RamCleaner-report-*.html").OrderByDescending(f => f.Name, StringComparer.Ordinal).Take(count).ToArray();
}
