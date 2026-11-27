using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Arbeitshelfer;

public partial class App : Application
{
    private const string MutexName = @"Local\Arbeitshelfer_EinzelInstanz";
    private const string ZeigenSignalName = @"Local\Arbeitshelfer_Zeigen";

    private Mutex? _einzelInstanz;
    private EventWaitHandle? _zeigenSignal;

    /// <summary>Wurde die App gerade durch ein Update neu gestartet?</summary>
    public static bool NachUpdate { get; private set; }

    public static string Ordner { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Arbeitshelfer");

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, a) =>
        {
            FehlerProtokollieren(a.Exception);
            MessageBox.Show($"Unerwarteter Fehler:\n{a.Exception.Message}\n\nDetails: {Path.Combine(Ordner, "fehler.log")}",
                "Arbeitshelfer", MessageBoxButton.OK, MessageBoxImage.Error);
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            if (a.ExceptionObject is Exception ex) FehlerProtokollieren(ex);
        };

        // Nur eine Instanz pro Benutzer. Ein zweiter Start holt den Knopf der laufenden Instanz nach vorn.
        _einzelInstanz = new Mutex(true, MutexName, out var neu);
        if (!neu)
        {
            try { EventWaitHandle.OpenExisting(ZeigenSignalName).Set(); } catch { /* ältere Version läuft */ }
            Shutdown();
            return;
        }

        _zeigenSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ZeigenSignalName);
        var warter = new Thread(() =>
        {
            while (_zeigenSignal.WaitOne())
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.HervorholenUndAusklappen());
        }) { IsBackground = true, Name = "Zeigen-Signal" };
        warter.Start();

        base.OnStartup(e);
        NachUpdate = e.Args.Contains("--aktualisiert");
        Aktualisierung.Aufraeumen();
        SessionEnding += (_, _) => (MainWindow as MainWindow)?.AllesSichern();
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    /// <summary>Startet die App neu (z. B. nach Umschalten der Darstellung).</summary>
    public void Neustarten(string? argument = null)
    {
        _einzelInstanz?.ReleaseMutex();
        _einzelInstanz?.Dispose();
        _einzelInstanz = null;
        if (Environment.ProcessPath is string pfad)
            Process.Start(new ProcessStartInfo(pfad, argument ?? "") { UseShellExecute = true });
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _einzelInstanz?.Dispose();
        base.OnExit(e);
    }

    public static void FehlerProtokollieren(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Ordner);
            File.AppendAllText(Path.Combine(Ordner, "fehler.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { /* nichts zu machen */ }
    }
}
