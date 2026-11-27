using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Arbeitshelfer;

/// <summary>
/// Kopiert Kennwörter in die Zwischenablage, ohne dass sie im
/// Zwischenablageverlauf (Win+V) oder in der Cloud-Zwischenablage landen,
/// (bewusst OHNE "ExcludeClipboardContentFromMonitorProcessing" – das sperrt
/// auch die Weitergabe an Remote-Desktop-/Citrix-Sitzungen),
/// und löscht sie nach einer Frist wieder – aber nur, wenn noch dasselbe
/// Kennwort drinsteht.
/// </summary>
public static class SichereZwischenablage
{
    private static DispatcherTimer? _timer;
    private static string? _letzterWert;

    public static void Kopieren(string text, int leerenNachSekunden)
    {
        var daten = new DataObject();
        daten.SetText(text, TextDataFormat.UnicodeText);
        daten.SetData("CanIncludeInClipboardHistory", NullDword());
        daten.SetData("CanUploadToCloudClipboard", NullDword());

        Wiederholen(() => Clipboard.SetDataObject(daten, copy: true));
        _letzterWert = text;

        _timer?.Stop();
        if (leerenNachSekunden <= 0) return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(leerenNachSekunden) };
        _timer.Tick += (_, _) =>
        {
            _timer!.Stop();
            Leeren();
        };
        _timer.Start();
    }

    /// <summary>Löscht die Zwischenablage, falls sie noch unser Kennwort enthält.</summary>
    public static void Leeren()
    {
        if (_letzterWert is null) return;
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == _letzterWert)
                Wiederholen(Clipboard.Clear);
        }
        catch { /* Zwischenablage gerade gesperrt – nicht kritisch */ }
        _letzterWert = null;
    }

    private static MemoryStream NullDword() => new(new byte[4]);

    private static void Wiederholen(Action aktion)
    {
        for (var i = 0; ; i++)
        {
            try { aktion(); return; }
            catch (System.Runtime.InteropServices.COMException) when (i < 5) { Thread.Sleep(50); }
        }
    }
}
