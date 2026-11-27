using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Arbeitshelfer;

public sealed record LapsErgebnis(string Kennwort, DateTime? Ablauf);

public sealed class LapsFehler(string meldung) : Exception(meldung);

/// <summary>
/// Fragt das Windows-LAPS-Kennwort eines Rechners aus dem Active Directory ab
/// (PowerShell-Modul "LAPS", Get-LapsADPassword). Läuft mit den Rechten des
/// angemeldeten Benutzers – wer im AD keine Leserechte hat, bekommt nichts.
/// </summary>
public static partial class LapsAbfrage
{
    // NetBIOS-/DNS-Rechnername: Buchstaben, Ziffern, Bindestrich, Punkt, Unterstrich.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_.]{0,254}$")]
    private static partial Regex GueltigerName();

    public static bool IstGueltig(string name) => GueltigerName().IsMatch(name);

    public static async Task<LapsErgebnis> AbfragenAsync(string rechner, CancellationToken ct = default)
    {
        rechner = rechner.Trim();
        if (!IstGueltig(rechner))
            throw new LapsFehler("Ungültiger Rechnername.");

        // Der Name ist oben validiert (keine Anführungszeichen möglich).
        var skript = $$"""
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            try { Import-Module LAPS -ErrorAction Stop } catch { [Console]::Error.Write('MODUL'); exit 2 }
            try { $r = Get-LapsADPassword -Identity '{{rechner}}' -AsPlainText }
            catch { [Console]::Error.Write($_.Exception.Message); exit 1 }
            if (-not $r -or -not $r.Password) { exit 3 }
            $ablauf = ''
            if ($r.ExpirationTimestamp) { $ablauf = $r.ExpirationTimestamp.ToString('o') }
            [Console]::Out.Write($r.Password + "`n" + $ablauf)
            """;

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(skript)));

        using var prozess = Process.Start(psi) ?? throw new LapsFehler("PowerShell konnte nicht gestartet werden.");

        using var zeitlimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        zeitlimit.CancelAfter(TimeSpan.FromSeconds(30));

        var ausgabe = prozess.StandardOutput.ReadToEndAsync(zeitlimit.Token);
        var fehler = prozess.StandardError.ReadToEndAsync(zeitlimit.Token);
        try
        {
            await prozess.WaitForExitAsync(zeitlimit.Token);
        }
        catch (OperationCanceledException)
        {
            try { prozess.Kill(entireProcessTree: true); } catch { /* egal */ }
            throw new LapsFehler("Zeitüberschreitung bei der Abfrage.");
        }

        var stdout = await ausgabe;
        var stderr = (await fehler).Trim();

        switch (prozess.ExitCode)
        {
            case 0:
                var teile = stdout.Split('\n', 2);
                DateTime? ablauf = teile.Length > 1 && DateTime.TryParse(teile[1].Trim(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : null;
                return new LapsErgebnis(teile[0], ablauf);
            case 2:
                throw new LapsFehler("PowerShell-Modul \"LAPS\" nicht gefunden (Windows LAPS nicht installiert?).");
            case 3:
                throw new LapsFehler($"Kein LAPS-Kennwort für {rechner} gefunden oder keine Berechtigung.");
            default:
                throw new LapsFehler(string.IsNullOrEmpty(stderr) ? "Abfrage fehlgeschlagen." : KuerzeFehler(stderr));
        }
    }

    private static string KuerzeFehler(string text)
    {
        var zeile = text.Split('\n')[0].Trim();
        return zeile.Length > 160 ? zeile[..160] + "…" : zeile;
    }
}
