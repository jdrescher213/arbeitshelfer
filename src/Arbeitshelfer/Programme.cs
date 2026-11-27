using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Arbeitshelfer;

public sealed record StartApp(string Name, string Start);

/// <summary>
/// Startet ein Programm oder holt sein Fenster nach vorn, wenn es schon läuft.
/// Die Programmliste kommt aus dem Startmenü (PowerShell Get-StartApps) und enthält
/// klassische Programme und Store-/MSIX-Apps; gestartet wird über shell:AppsFolder\&lt;AppID&gt;.
/// </summary>
public static class Programme
{
    public const string AppsFolder = @"shell:AppsFolder\";

    public static async Task<List<StartApp>> StartAppsAsync()
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                     "[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-StartApps | Sort-Object Name | " +
                     "ForEach-Object { $_.Name + \"`t\" + $_.AppID }" })
            psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell nicht gestartet.");
        var ausgabe = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();

        var liste = new List<StartApp>();
        foreach (var zeile in ausgabe.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var teile = zeile.Split('\t', 2);
            if (teile.Length == 2 && teile[0].Length > 0 && teile[1].Length > 0)
                liste.Add(new StartApp(teile[0], AppsFolder + teile[1]));
        }
        return liste;
    }

    /// <summary>Prozessname zum Wiederfinden des Fensters raten (z. B. "Claude" → "claude").</summary>
    public static string ProzessRaten(string name, string start)
    {
        var pfad = start.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase) ? start[AppsFolder.Length..] : start;
        if (pfad.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(pfad);
        return new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    public static void OeffnenOderHervorholen(string start, string? prozess)
    {
        if (!string.IsNullOrWhiteSpace(prozess) && Hervorholen(prozess)) return;

        if (start.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase))
            Process.Start(new ProcessStartInfo("explorer.exe", start) { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo(start) { UseShellExecute = true });
        // Läuft das Programm schon, aber nur im Infobereich, zeigt der zweite Start
        // bei den meisten Programmen (z. B. Electron-Apps) das vorhandene Fenster.
    }

    private static bool Hervorholen(string prozess)
    {
        foreach (var p in Process.GetProcessesByName(prozess))
        {
            using (p)
            {
                var fenster = p.MainWindowHandle;
                if (fenster == IntPtr.Zero || !IsWindowVisible(fenster)) continue;
                ShowWindow(fenster, IsIconic(fenster) ? SW_RESTORE : SW_SHOW);
                SetForegroundWindow(fenster);
                return true;
            }
        }
        return false;
    }

    private const int SW_SHOW = 5, SW_RESTORE = 9;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
}
