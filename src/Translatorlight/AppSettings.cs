using System.Text.Json;
using System.Text.Json.Serialization;

namespace Translatorlight;

/// <summary>
/// User preferences, persisted as JSON in %APPDATA%\Translatorlight\settings.json.
/// Loading never throws: a missing or damaged file simply yields the defaults.
/// </summary>
internal sealed class AppSettings
{
    internal const string DirectionAuto = "auto";
    internal const string DirectionEnglishToRussian = "en-ru";
    internal const string DirectionRussianToEnglish = "ru-en";

    internal const string ServiceAuto = "auto";

    /// <summary>Where the widget sits. Null until it has been moved or shown for the first time.</summary>
    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    public int? WindowWidth { get; set; }

    /// <summary>Keep the widget above other windows, so there is always something to drop onto.</summary>
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>Put the translation into the clipboard as well, so Ctrl+V works straight away.</summary>
    public bool CopyToClipboard { get; set; } = true;

    /// <summary>"auto", "en-ru" or "ru-en".</summary>
    public string Direction { get; set; } = DirectionAuto;

    /// <summary>Preferred service: "auto", "Google", "MyMemory" or "Lingva". The rest stay as fallbacks.</summary>
    public string Service { get; set; } = ServiceAuto;

    /// <summary>Fade the widget slightly while it is not the active window.</summary>
    public bool DimWhenInactive { get; set; } = true;

    /// <summary>Start with the widget hidden, leaving only the tray icon.</summary>
    public bool StartHidden { get; set; }

    [JsonIgnore]
    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Translatorlight");

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(DirectoryPath, "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), SerializerOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Fall through to defaults - a broken settings file must never block startup.
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(DirectoryPath);

            // Write to a temporary file first so a crash mid-write cannot truncate the settings.
            var temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, SerializerOptions));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience; failing to persist them is not worth interrupting the user.
        }
    }

    private void Normalize()
    {
        if (Direction is not (DirectionAuto or DirectionEnglishToRussian or DirectionRussianToEnglish))
        {
            Direction = DirectionAuto;
        }

        if (string.IsNullOrWhiteSpace(Service))
        {
            Service = ServiceAuto;
        }

        // Zero or negative sizes come from a hand-edited file; treat them as "never saved".
        if (WindowWidth.HasValue && WindowWidth.Value <= 0)
        {
            WindowWidth = null;
        }
    }
}
