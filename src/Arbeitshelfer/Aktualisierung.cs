using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Arbeitshelfer;

public sealed record UpdateInfo(Version Version, Uri Adresse, string? Sha256);

/// <summary>
/// Selbst-Aktualisierung über GitHub-Releases (öffentliches Repo, kein Schlüssel nötig).
/// Ablauf: neue .exe neben die laufende laden, Prüfsumme prüfen, laufende .exe umbenennen
/// (geht unter Windows auch während sie läuft), neue an ihre Stelle, neu starten.
/// </summary>
public static class Aktualisierung
{
    private const string Repo = "jdrescher213/arbeitshelfer";
    private const string DateiName = "Arbeitshelfer.exe";

    private static readonly HttpClient Client = ClientErstellen();

    public static Version Aktuell { get; } = Normalisieren(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));

    public static string AktuellText => $"{Aktuell.Major}.{Aktuell.Minor}.{Aktuell.Build}";

    private static HttpClient ClientErstellen()
    {
        // Firmen-Proxy mit Windows-Anmeldung durchreichen.
        var handler = new HttpClientHandler
        {
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Arbeitshelfer", AktuellText));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static Version Normalisieren(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Liefert das neueste Release, falls es neuer als die laufende Version ist.</summary>
    public static async Task<UpdateInfo?> PruefenAsync(CancellationToken ct = default)
    {
        using var antwort = await Client.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        if (antwort.StatusCode == HttpStatusCode.NotFound) return null; // noch kein Release (oder Repo privat)
        antwort.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await antwort.Content.ReadAsStringAsync(ct));
        var tag = doc.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
        if (tag is null || !Version.TryParse(tag, out var version)) return null;
        version = Normalisieren(version);
        if (version <= Aktuell) return null;

        foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), DateiName, StringComparison.OrdinalIgnoreCase))
                continue;
            var url = asset.GetProperty("browser_download_url").GetString();
            string? sha = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is string d &&
                d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = d["sha256:".Length..];
            return url is null ? null : new UpdateInfo(version, new Uri(url), sha);
        }
        return null;
    }

    /// <summary>Lädt die neue Version und tauscht die .exe aus. Danach muss die App neu gestartet werden.</summary>
    public static async Task InstallierenAsync(UpdateInfo update, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Programmpfad unbekannt.");
        var neu = exe + ".neu";
        var alt = exe + ".alt";

        try
        {
            using (var antwort = await Client.GetAsync(update.Adresse, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                antwort.EnsureSuccessStatusCode();
                await using var ziel = File.Create(neu);
                await antwort.Content.CopyToAsync(ziel, ct);
            }
        }
        catch (UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Keine Schreibrechte im Ordner {Path.GetDirectoryName(exe)}. " +
                "Die Arbeitshelfer.exe bitte in einen eigenen Ordner legen (z. B. C:\\Tools oder Dokumente).");
        }

        try
        {
            if (new FileInfo(neu).Length < 1_000_000)
                throw new InvalidOperationException("Heruntergeladene Datei ist unvollständig.");
            if (update.Sha256 is string erwartet)
            {
                await using var datei = File.OpenRead(neu);
                var ist = Convert.ToHexString(await SHA256.HashDataAsync(datei, ct));
                if (!ist.Equals(erwartet, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Prüfsumme stimmt nicht – Update verworfen.");
            }

            if (File.Exists(alt)) File.Delete(alt);
            File.Move(exe, alt);       // laufende .exe darf umbenannt werden
            try
            {
                File.Move(neu, exe);
            }
            catch
            {
                File.Move(alt, exe);   // zurückrollen
                throw;
            }
        }
        catch
        {
            try { File.Delete(neu); } catch { /* egal */ }
            throw;
        }
    }

    /// <summary>Beim Start: Reste eines vorherigen Updates entfernen.</summary>
    public static void Aufraeumen()
    {
        if (Environment.ProcessPath is not string exe) return;
        _ = Task.Run(async () =>
        {
            // Der alte Prozess braucht evtl. noch einen Moment, bis er beendet ist.
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    foreach (var rest in new[] { exe + ".alt", exe + ".neu" })
                        if (File.Exists(rest)) File.Delete(rest);
                    return;
                }
                catch
                {
                    await Task.Delay(2000);
                }
            }
        });
    }
}
