using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arbeitshelfer;

/// <summary>Einstellungen, gespeichert in %APPDATA%\Arbeitshelfer\einstellungen.json.</summary>
public sealed class Einstellungen
{
    public double? Links { get; set; }
    public double? Oben { get; set; }

    /// <summary>Vergrößerung von Knopf und Leiste (1.0 = 100 %).</summary>
    public double Groesse { get; set; } = 2.0;

    /// <summary>Systemweites Tastenkürzel zum Auf-/Zuklappen (Name aus Tastenkuerzel.Auswahl).</summary>
    public string Tastenkuerzel { get; set; } = "Win+Q";

    /// <summary>Benutzername für LAPS-Eintippen (Benutzer TAB Kennwort ENTER).</summary>
    public string LapsBenutzer { get; set; } = @".\administrator";

    /// <summary>Nach dem Kennwort Enter drücken.</summary>
    public bool EintippenMitEnter { get; set; } = true;

    /// <summary>Adresse der KI, öffnet im Standardbrowser.</summary>
    public string KiAdresse { get; set; } = "";

    /// <summary>Browser für die KI: "Standardbrowser", "Firefox", "Chrome" oder "Edge".</summary>
    public string KiBrowser { get; set; } = "Standardbrowser";

    /// <summary>Profil im gewählten Browser (Firefox: Profilname, Chrome/Edge: Profilordner).</summary>
    public string? KiProfil { get; set; }

    /// <summary>Nach wie vielen Sekunden ein kopiertes Kennwort aus der Zwischenablage gelöscht wird (0 = nie).</summary>
    public int ZwischenablageLeerenNachSekunden { get; set; } = 60;

    /// <summary>Wie lange ein abgefragtes Kennwort sichtbar bleibt (Sekunden).</summary>
    public int KennwortAnzeigenSekunden { get; set; } = 60;

    /// <summary>Nur Rechnernamen – Kennwörter werden nie gespeichert.</summary>
    public List<string> LetzteRechner { get; set; } = new();

    /// <summary>"Laps", "ToDo" oder "Einstellungen" – welcher Bereich beim Ausklappen sichtbar ist.</summary>
    public string Bereich { get; set; } = "Laps";

    public bool ErledigteEinblenden { get; set; }

    /// <summary>Eigene Tastatureingaben (Symbole unter den letzten LAPS-Abfragen).</summary>
    public List<Eingabe> Eingaben { get; set; } = new();

    /// <summary>Neue Versionen automatisch installieren (wenn eingeklappt und keine Zeit läuft).</summary>
    public bool AutoUpdate { get; set; } = true;

    /// <summary>Ohne Transparenz zeichnen – nötig, wenn der Knopf (z. B. per Remote-Desktop/Citrix) unsichtbar bleibt.</summary>
    public bool EinfacheDarstellung { get; set; }
}

public abstract class Beobachtbar : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Melden([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string Dauer(TimeSpan t) => $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
}

/// <summary>
/// Eine selbst definierte Tastatureingabe: [Benutzer TAB] Kennwort [ENTER].
/// Wird – wie alle Einstellungen – verschlüsselt gespeichert.
/// </summary>
public sealed class Eingabe : Beobachtbar
{
    private string _name = "Neue Eingabe";
    private string _symbol = "Tastatur";
    private string _benutzer = "";
    private string _kennwort = "";
    private bool _mitEnter;

    public string Name { get => _name; set { _name = value; Melden(); } }

    /// <summary>Name eines eingebauten Symbols (siehe <see cref="Symbole"/>) oder "datei:&lt;Pfad&gt;".</summary>
    public string Symbol { get => _symbol; set { _symbol = value; Melden(); Melden(nameof(Bild)); } }

    public string Benutzer { get => _benutzer; set { _benutzer = value; Melden(); Melden(nameof(Beschreibung)); } }
    public string Kennwort { get => _kennwort; set { _kennwort = value; Melden(); } }
    public bool MitEnter { get => _mitEnter; set { _mitEnter = value; Melden(); Melden(nameof(Beschreibung)); } }

    [JsonIgnore] public System.Windows.Media.ImageSource Bild => Symbole.Bild(Symbol);

    [JsonIgnore]
    public string Beschreibung =>
        $"{Name}: {(Benutzer.Length > 0 ? Benutzer + " ⇥ " : "")}Kennwort{(MitEnter ? " ↵" : "")}";
}

/// <summary>Ein Zeitabschnitt (Play bis Pause).</summary>
public sealed class Buchung : Beobachtbar
{
    private DateTime? _ende;

    public DateTime Start { get; set; }

    public DateTime? Ende
    {
        get => _ende;
        set { _ende = value; Aktualisieren(); }
    }

    [JsonIgnore] public TimeSpan Laenge => (Ende ?? DateTime.Now) - Start;

    [JsonIgnore]
    public string Anzeige =>
        $"{Start:ddd dd.MM.  HH:mm} – {(Ende is DateTime e ? e.ToString(e.Date == Start.Date ? "HH:mm" : "dd.MM. HH:mm") : "läuft")}";

    [JsonIgnore] public string LaengeText => Dauer(Laenge);

    public void Aktualisieren()
    {
        Melden(nameof(Anzeige));
        Melden(nameof(LaengeText));
    }
}

public sealed class TodoEintrag : Beobachtbar
{
    private string _text = "";
    private bool _erledigt;
    private bool _buchungenOffen;

    public string Text
    {
        get => _text;
        set { _text = value; Melden(); }
    }

    public bool Erledigt
    {
        get => _erledigt;
        set
        {
            if (_erledigt == value) return;
            _erledigt = value;
            ErledigtAm = value ? DateTime.Now : null;
            if (value) Pausieren();
            Melden();
        }
    }

    public DateTime Erstellt { get; set; } = DateTime.Now;
    public DateTime? ErledigtAm { get; set; }

    public ObservableCollection<Buchung> Buchungen { get; set; } = new();

    [JsonIgnore] public bool Laeuft => Buchungen.Any(b => b.Ende is null);
    [JsonIgnore] public bool HatZeit => Buchungen.Count > 0;
    [JsonIgnore] public TimeSpan Summe => Buchungen.Aggregate(TimeSpan.Zero, (s, b) => s + b.Laenge);
    [JsonIgnore] public string SummeText => Dauer(Summe);

    /// <summary>Aufgeklappte Einzelbuchungen (nur Anzeige).</summary>
    [JsonIgnore]
    public bool BuchungenOffen
    {
        get => _buchungenOffen;
        set { _buchungenOffen = value; Melden(); }
    }

    public void Starten()
    {
        if (Laeuft) return;
        Buchungen.Add(new Buchung { Start = DateTime.Now });
        Aktualisieren();
    }

    public void Pausieren(DateTime? ende = null)
    {
        foreach (var b in Buchungen.Where(b => b.Ende is null))
            b.Ende = ende ?? DateTime.Now;
        Aktualisieren();
    }

    public void Aktualisieren()
    {
        foreach (var b in Buchungen.Where(b => b.Ende is null)) b.Aktualisieren();
        Melden(nameof(Laeuft));
        Melden(nameof(HatZeit));
        Melden(nameof(Summe));
        Melden(nameof(SummeText));
    }
}

public static class Speicher
{
    private static string Ordner => App.Ordner;

    private static readonly JsonSerializerOptions Optionen = new() { WriteIndented = true };

    public static Einstellungen LadeEinstellungen() => Lade<Einstellungen>("einstellungen.json") ?? new Einstellungen();
    public static void SpeichereEinstellungen(Einstellungen e) => Speichere("einstellungen.json", e);

    public static List<TodoEintrag> LadeTodos()
    {
        var todos = Lade<List<TodoEintrag>>("todos.json") ?? new List<TodoEintrag>();

        // Lief beim letzten Beenden (Absturz, Herunterfahren) noch eine Zeit, endet sie
        // zum Zeitpunkt der letzten Speicherung (wird während der Erfassung minütlich gespeichert).
        var pfad = Path.Combine(Ordner, "todos.json");
        var zuletzt = File.Exists(pfad) ? File.GetLastWriteTime(pfad) : DateTime.Now;
        foreach (var t in todos)
            foreach (var b in t.Buchungen.Where(b => b.Ende is null))
                b.Ende = zuletzt > b.Start ? zuletzt : b.Start;
        return todos;
    }

    public static void SpeichereTodos(IEnumerable<TodoEintrag> todos) => Speichere("todos.json", todos.ToList());

    // Verschlüsselung mit Windows-DPAPI (Benutzerkonto): kein eigenes Startpasswort nötig.
    // Lesbar nur für denselben Windows-Benutzer; andere Benutzer und Kopien der Datei bekommen nur Datensalat.
    private static readonly byte[] Kennung = "Arbeitshelfer/1"u8.ToArray();
    private static readonly byte[] Kopf = "AHv1"u8.ToArray();

    private static T? Lade<T>(string datei)
    {
        try
        {
            var pfad = Path.Combine(Ordner, datei);
            if (!File.Exists(pfad)) return default;
            var bytes = File.ReadAllBytes(pfad);
            string json;
            if (bytes.AsSpan().StartsWith(Kopf))
                json = Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes[Kopf.Length..], Kennung, DataProtectionScope.CurrentUser));
            else
                json = Encoding.UTF8.GetString(bytes); // ältere, unverschlüsselte Datei – wird beim nächsten Speichern verschlüsselt
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            App.FehlerProtokollieren(ex);
            // Unlesbar (z. B. anderer Windows-Benutzer): Datei aufheben statt beim nächsten Speichern zu überschreiben.
            try
            {
                var pfad = Path.Combine(Ordner, datei);
                File.Move(pfad, $"{pfad}.unlesbar-{DateTime.Now:yyyyMMdd-HHmmss}");
            }
            catch { /* egal */ }
            return default;
        }
    }

    private static void Speichere<T>(string datei, T wert)
    {
        try
        {
            Directory.CreateDirectory(Ordner);
            var pfad = Path.Combine(Ordner, datei);
            var temp = pfad + ".tmp";
            var verschluesselt = ProtectedData.Protect(
                JsonSerializer.SerializeToUtf8Bytes(wert, Optionen), Kennung, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(temp, [.. Kopf, .. verschluesselt]);
            File.Move(temp, pfad, overwrite: true);
        }
        catch (Exception ex)
        {
            App.FehlerProtokollieren(ex); // Speichern ist nicht kritisch – beim nächsten Mal erneut versuchen.
        }
    }
}
