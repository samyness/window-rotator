using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Fensterwechler;

public static class AppPaths
{
    public const string AppName = "WindowRotator";
    public static string Roaming { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
    public static string LocalData { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    public static string SettingsFile => Path.Combine(Roaming, "settings.json");
    public static string ProfileDirectory => Path.Combine(Roaming, "profiles");
}

/// <summary>Versioned JSON storage for settings and profiles.</summary>
public static class Store
{
    public const int SchemaVersion = 1;
    static readonly string[] ReservedNames = ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })];

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings LoadSettings()
    {
        if (!File.Exists(AppPaths.SettingsFile)) return new AppSettings();
        try
        {
            return Read<AppSettings>(AppPaths.SettingsFile);
        }
        catch (InvalidDataException ex)
        {
            Log.Error($"Einstellungen konnten nicht geladen werden, Standardwerte aktiv: {ex.Message}");
            return new AppSettings();
        }
    }

    public static void SaveSettings(AppSettings settings) => Write(AppPaths.SettingsFile, settings);

    public static IEnumerable<string> ProfileNames() =>
        Directory.Exists(AppPaths.ProfileDirectory)
            ? Directory.EnumerateFiles(AppPaths.ProfileDirectory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.CurrentCultureIgnoreCase)
            : [];

    public static string ProfilePath(string name) => Path.Combine(AppPaths.ProfileDirectory, name + ".json");

    /// <exception cref="InvalidDataException">Profile missing, unreadable, corrupt or from a newer version.</exception>
    public static RotationProfile LoadProfile(string name) => LoadProfileFile(ProfilePath(name), name);

    public static RotationProfile LoadProfileFile(string path, string? name = null)
    {
        if (!File.Exists(path)) throw new InvalidDataException($"Profil \"{name ?? path}\" existiert nicht.");
        var profile = Read<RotationProfile>(path);
        profile.Name = name ?? Path.GetFileNameWithoutExtension(path);
        Sanitize(profile);
        return profile;
    }

    public static void SaveProfile(RotationProfile profile)
    {
        if (ValidateProfileName(profile.Name) is { } error) throw new ArgumentException(error);
        Sanitize(profile);
        Write(ProfilePath(profile.Name), profile);
    }

    public static void DeleteProfile(string name) => File.Delete(ProfilePath(name));

    /// <summary>Returns an error text, or null if the name is usable as a file name on every Windows version.</summary>
    public static string? ValidateProfileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Bitte einen Profilnamen eingeben.";
        if (name.Length > 60) return "Der Profilname darf höchstens 60 Zeichen lang sein.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Der Profilname enthält unzulässige Zeichen (\\ / : * ? \" < > |).";
        if (name != name.Trim() || name.EndsWith('.')) return "Der Profilname darf nicht mit Leerzeichen oder Punkt beginnen/enden.";
        if (ReservedNames.Contains(name.ToUpperInvariant())) return "Dieser Name ist unter Windows reserviert.";
        return null;
    }

    /// <summary>Loaded data is validated like UI input.</summary>
    static void Sanitize(RotationProfile profile)
    {
        profile.Entries = profile.Entries?.Where(e => e != null && !string.IsNullOrWhiteSpace(e.ProcessName)).ToList() ?? [];
        foreach (var e in profile.Entries)
        {
            e.DurationSeconds = Math.Clamp(e.DurationSeconds, RotationProfile.MinDuration, RotationProfile.MaxDuration);
            e.Title ??= "";
            e.ClassName ??= "";
        }
        profile.RetryCount = Math.Clamp(profile.RetryCount, 0, 10);
        if (!Enum.IsDefined(profile.FailureStrategy)) profile.FailureStrategy = FailureStrategy.RetryThenSkip;
    }

    static T Read<T>(string path) where T : class
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("JSON-Objekt erwartet.");
            if (doc.RootElement.TryGetProperty("SchemaVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int version) && version > SchemaVersion)
                throw new InvalidDataException($"{Path.GetFileName(path)} stammt aus einer neueren Programmversion (Schema {version}).");
            return doc.RootElement.Deserialize<T>(Json) ?? throw new InvalidDataException($"{Path.GetFileName(path)} ist leer.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Keep a copy so a later save cannot silently destroy the user's data.
            string backup = $"{path}.defekt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Copy(path, backup, overwrite: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw new InvalidDataException($"{Path.GetFileName(path)} ist beschädigt oder nicht lesbar (Sicherung: {Path.GetFileName(backup)}): {ex.Message}", ex);
        }
    }

    /// <summary>Atomic write: a crash or power loss leaves either the old or the new file, never half of one.</summary>
    static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Start at Windows logon via HKCU\...\Run (no admin rights, no service).</summary>
public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    static string Command => $"\"{Environment.ProcessPath}\" --tray";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppPaths.AppName) is string value && value.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(AppPaths.AppName, Command);
            else key.DeleteValue(AppPaths.AppName, throwOnMissingValue: false);
        }
    }
}
