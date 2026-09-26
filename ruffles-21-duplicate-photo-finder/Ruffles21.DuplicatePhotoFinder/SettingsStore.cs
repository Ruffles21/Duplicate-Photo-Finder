using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ruffles21.DuplicatePhotoFinder;

public sealed class Settings
{
    public List<string> Folders { get; set; } = new();
    public string Preferred { get; set; } = "";
    public string Backups { get; set; } = "";
    public int Rule
    {
        get; set;
    }
    public string View { get; set; } = "Gallery";

    public void Normalize()
    {
        Folders = (Folders ?? new()).Where(ValidFolder).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Preferred = NormalizeFolders(Preferred);
        Backups = NormalizeFolders(Backups);
        Rule = Math.Clamp(Rule, 0, 5);
        if (View is not ("Gallery" or "Compact" or "List"))
            View = "Gallery";
    }

    private static bool ValidFolder(string? value)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) && Path.GetFullPath(value).Length > 0;
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (IOException) { return false; }
    }

    private static string NormalizeFolders(string? value) => string.Join("; ",
        (value ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Where(ValidFolder));
}

public static class SettingsStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "Ruffles21DuplicatePhotoFinder", "settings.json");

    public static Settings Load(string path, string? legacyPath = null)
    {
        if (legacyPath == null && string.Equals(path, DefaultPath, StringComparison.OrdinalIgnoreCase))
            legacyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LuigiPhotoKeeper", "settings.json");
        // Read older preferences once; future saves go to the new product's location.
        string source = !File.Exists(path) && legacyPath != null && File.Exists(legacyPath) ? legacyPath : path;
        Settings result;
        try
        {
            result = File.Exists(source) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(source)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { result = new(); }
        result.Normalize();
        return result;
    }

    public static void Save(string path, Settings settings)
    {
        settings.Normalize();
        AtomicFile.WriteText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal static class AtomicFile
{
    public static void WriteText(string path, string text, bool byteOrderMark = false)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(byteOrderMark));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
