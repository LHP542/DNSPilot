using System.Text.Json;
using DnsPilot.Models;
using NLog;

namespace DnsPilot.Services;

/// <summary>
/// Prüfhistorie aller Adressen, gespeichert unter
/// <c>%LOCALAPPDATA%\DnsPilot\sightings.json</c>.
///
/// Gespeichert werden nur Adresse, Zeitpunkte und Zähler — keine Namen, keine
/// Zoneninhalte. Die Datei ist die Grundlage für „seit 30 Tagen nicht
/// erreicht" und damit für jede Aufräum-Empfehlung; ohne sie fängt jede
/// Sitzung bei null an.
/// </summary>
public sealed class SightingStore
{
    private static readonly ILogger Log = LogManager.GetCurrentClassLogger();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, HostSighting> _entries = new(StringComparer.Ordinal);
    private bool _dirty;

    public SightingStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DnsPilot", "sightings.json"))
    {
    }

    /// <summary>Konstruktor mit explizitem Pfad — für Tests.</summary>
    public SightingStore(string path) => _path = path;

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Liest die Datei. Defekt = nach <c>.broken</c> sichern, leer weiter.</summary>
    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return;

                var loaded = JsonSerializer.Deserialize<Dictionary<string, HostSighting>>(File.ReadAllText(_path), JsonOptions);
                _entries = new Dictionary<string, HostSighting>(loaded ?? [], StringComparer.Ordinal);
                Log.Info("Prüfhistorie mit {0} Adressen aus {1} geladen.", _entries.Count, _path);
            }
            catch (JsonException ex)
            {
                Log.Error(ex, "Prüfhistorie {0} ist defekt.", _path);
                JsonFileStore.Quarantine(_path);
                _entries = new(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                // Gesperrt oder kurz nicht lesbar: Inhalt ist intakt, nichts anfassen.
                Log.Error(ex, "Prüfhistorie {0} konnte nicht gelesen werden.", _path);
            }
        }
    }

    /// <summary>Historie zu einer Adresse, oder null.</summary>
    public HostSighting? Get(string address)
    {
        lock (_gate) return _entries.TryGetValue(address, out var s) ? s : null;
    }

    /// <summary>Trägt ein Prüfergebnis ein.</summary>
    public HostSighting Record(string address, bool online, string? method, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(address, out var s))
            {
                s = new HostSighting { FirstCheckedUtc = nowUtc };
                _entries[address] = s;
            }

            s.Checks++;
            s.LastCheckedUtc = nowUtc;
            if (online)
            {
                s.LastOnlineUtc = nowUtc;
                s.LastMethod = method;
                s.FailuresSinceOnline = 0;
            }
            else
            {
                s.FailuresSinceOnline++;
            }

            _dirty = true;
            return s;
        }
    }

    /// <summary>Schreibt die Datei, wenn sich etwas geändert hat. Fehler werden geloggt, nicht geworfen.</summary>
    public void Save()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(_entries, JsonOptions);
            _dirty = false;
        }

        try
        {
            JsonFileStore.WriteAtomic(_path, json);
            Log.Debug("Prüfhistorie nach {0} geschrieben.", _path);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Prüfhistorie konnte nicht nach {0} geschrieben werden.", _path);
            lock (_gate) _dirty = true;
        }
    }
}
