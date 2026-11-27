using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Arbeitshelfer;

public sealed record BrowserProfil(string Anzeige, string Wert);

/// <summary>
/// Öffnet Adressen in einem bestimmten Browser und Profil.
/// Firefox: -P "Profilname" – läuft das Profil schon, kommt ein neuer Tab dorthin,
/// sonst startet es zusätzlich (das andere Profil bleibt unberührt).
/// Chrome/Edge: --profile-directory="Profile 1".
/// </summary>
public static class Browser
{
    public const string Standard = "Standardbrowser";

    public static readonly string[] Arten = { Standard, "Firefox", "Chrome", "Edge" };

    private static string LokaleDaten => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static List<BrowserProfil> Profile(string art)
    {
        try
        {
            return art switch
            {
                "Firefox" => FirefoxProfile(),
                "Chrome" => ChromiumProfile(Path.Combine(LokaleDaten, @"Google\Chrome\User Data\Local State")),
                "Edge" => ChromiumProfile(Path.Combine(LokaleDaten, @"Microsoft\Edge\User Data\Local State")),
                _ => new(),
            };
        }
        catch (Exception ex)
        {
            App.FehlerProtokollieren(ex);
            return new();
        }
    }

    public static void Oeffnen(string adresse, string art, string? profil)
    {
        if (art == Standard)
        {
            Process.Start(new ProcessStartInfo(adresse) { UseShellExecute = true });
            return;
        }

        var exe = art switch
        {
            "Firefox" => ProgrammPfad("firefox.exe", @"Mozilla Firefox\firefox.exe"),
            "Chrome" => ProgrammPfad("chrome.exe", @"Google\Chrome\Application\chrome.exe"),
            "Edge" => ProgrammPfad("msedge.exe", @"Microsoft\Edge\Application\msedge.exe"),
            _ => null,
        } ?? throw new InvalidOperationException($"{art} wurde auf diesem Rechner nicht gefunden.");

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (!string.IsNullOrEmpty(profil))
        {
            if (art == "Firefox")
            {
                psi.ArgumentList.Add("-P");
                psi.ArgumentList.Add(profil);
            }
            else
            {
                psi.ArgumentList.Add($"--profile-directory={profil}");
            }
        }
        psi.ArgumentList.Add(adresse);
        Process.Start(psi);
    }

    /// <summary>Firefox-Profile aus %APPDATA%\Mozilla\Firefox\profiles.ini.</summary>
    private static List<BrowserProfil> FirefoxProfile()
    {
        var ini = Path.Combine(Roaming, @"Mozilla\Firefox\profiles.ini");
        var liste = new List<BrowserProfil>();
        if (!File.Exists(ini)) return liste;

        string? abschnitt = null, name = null;
        var standard = false;
        void Uebernehmen()
        {
            if (abschnitt?.StartsWith("Profile", StringComparison.OrdinalIgnoreCase) == true && name is not null)
                liste.Add(new BrowserProfil(standard ? $"{name}  (Standard)" : name, name));
            name = null;
            standard = false;
        }

        foreach (var roh in File.ReadAllLines(ini))
        {
            var zeile = roh.Trim();
            if (zeile.StartsWith('[') && zeile.EndsWith(']'))
            {
                Uebernehmen();
                abschnitt = zeile[1..^1];
            }
            else if (zeile.StartsWith("Name=", StringComparison.OrdinalIgnoreCase)) name = zeile[5..];
            else if (zeile.Equals("Default=1", StringComparison.OrdinalIgnoreCase)) standard = true;
        }
        Uebernehmen();
        return liste;
    }

    /// <summary>Chrome-/Edge-Profile aus "Local State" (Ordnername + angezeigter Name).</summary>
    private static List<BrowserProfil> ChromiumProfile(string localState)
    {
        var liste = new List<BrowserProfil>();
        if (!File.Exists(localState)) return liste;
        using var stream = new FileStream(localState, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("profile", out var profil) ||
            !profil.TryGetProperty("info_cache", out var cache)) return liste;
        foreach (var eintrag in cache.EnumerateObject())
        {
            var anzeige = eintrag.Value.TryGetProperty("name", out var n) ? n.GetString() : null;
            liste.Add(new BrowserProfil($"{anzeige ?? eintrag.Name}  ({eintrag.Name})", eintrag.Name));
        }
        return liste;
    }

    private static string? ProgrammPfad(string exeName, string unterProgramme)
    {
        foreach (var wurzel in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var k = wurzel.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exeName}");
            if (k?.GetValue(null) is string pfad && File.Exists(pfad.Trim('"'))) return pfad.Trim('"');
        }
        foreach (var basis in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     LokaleDaten,
                 })
        {
            var pfad = Path.Combine(basis, unterProgramme);
            if (File.Exists(pfad)) return pfad;
        }
        return null;
    }
}
