using System.Text.Json;
using DnsPilot.Models;
using NLog;

namespace DnsPilot.Services;

/// <summary>
/// Lädt und speichert <see cref="AppSettings"/> als JSON unter
/// <c>%LOCALAPPDATA%\DnsPilot\settings.json</c>.
/// </summary>
public sealed class SettingsService
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    /// <summary>Die aktuell geladenen Einstellungen. Nie null.</summary>
    public AppSettings Current { get; private set; } = new();

    /// <summary>Wird gefeuert, wenn die Einstellungen ersetzt wurden.</summary>
    public event EventHandler? Changed;

    public SettingsService() : this(DefaultPath()) { }

    /// <summary>Konstruktor mit explizitem Pfad — für Tests.</summary>
    public SettingsService(string path) => _path = path;

    /// <summary>Standardablage im lokalen Anwendungsdatenverzeichnis.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DnsPilot", "settings.json");

    public string FilePath => _path;

    /// <summary>
    /// Liest die Datei. Fehlt sie, wird mit Standardwerten gestartet. Ist sie
    /// defekt, wandert sie nach <c>.broken</c> — und zwar nur bei
    /// <see cref="JsonException"/>, nicht bei IO-Fehlern.
    /// </summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Log.Info("Keine Einstellungsdatei unter {0} — Start mit Standardwerten.", _path);
                Current = new AppSettings();
                return Current;
            }

            string json = File.ReadAllText(_path);
            Current = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            Log.Info("Einstellungen aus {0} geladen.", _path);
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "Einstellungsdatei {0} ist defekt.", _path);
            JsonFileStore.Quarantine(_path);
            Current = new AppSettings();
        }
        catch (Exception ex)
        {
            // Datei gesperrt, Profil noch nicht bereit, Netzlaufwerk weg: Inhalt
            // ist in Ordnung, nur gerade nicht lesbar. Nichts anfassen.
            Log.Error(ex, "Einstellungen aus {0} konnten nicht gelesen werden — Standardwerte für diese Sitzung.", _path);
            Current = new AppSettings();
        }

        return Current;
    }

    /// <summary>Ersetzt die aktuellen Einstellungen und speichert sie.</summary>
    public void Replace(AppSettings settings)
    {
        Current = settings;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Schreibt die aktuellen Einstellungen. Fehler werden geloggt, aber nicht
    /// geworfen: eine nicht speicherbare Einstellung darf das Werkzeug nicht
    /// beim Beenden aufhalten.
    /// </summary>
    public void Save()
    {
        try
        {
            JsonFileStore.WriteAtomic(_path, JsonSerializer.Serialize(Current, JsonOptions));
            Log.Debug("Einstellungen nach {0} geschrieben.", _path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Einstellungen konnten nicht nach {0} geschrieben werden.", _path);
        }
    }
}
