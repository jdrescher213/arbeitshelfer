using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Arbeitshelfer;

public partial class MainWindow : Window
{
    private const int MaxVerlauf = 3;
    private const string AutostartSchluessel = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutostartName = "Arbeitshelfer";

    private readonly Einstellungen _einstellungen = Speicher.LadeEinstellungen();
    private readonly ObservableCollection<TodoEintrag> _todos;
    private readonly ICollectionView _todoAnsicht;
    private readonly ObservableCollection<string> _verlauf;
    private readonly ObservableCollection<Eingabe> _eingaben;

    private bool _ausgeklappt;
    private bool _nachOben;      // Leiste öffnet oberhalb des Knopfes (Knopf nahe unterem Rand)
    private double _ankerUnten;  // bei _nachOben: Unterkante bleibt fest, wenn die Leiste wächst
    private bool _positioniere;
    private bool _initialisiert;

    private Point? _druckPunkt;
    private bool _abfrageLaeuft;
    private string? _angezeigtesKennwort;

    private readonly DispatcherTimer _sekundenTakt = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _kennwortTimer = new();
    private int _takte;

    private readonly Forms.NotifyIcon _symbol;
    private readonly Tastenkuerzel _tastenkuerzel = new();
    private readonly Eintipper _eintipper = new();
    private readonly bool _ersterStart;

    private UpdateInfo? _wartendesUpdate;
    private bool _updateLaeuft;
    private DateTime _naechstePruefung = DateTime.Now.AddSeconds(30);

    public MainWindow()
    {
        InitializeComponent();

        _ersterStart = _einstellungen.Links is null;
        if (_einstellungen.EinfacheDarstellung) EinfacheDarstellungAnwenden();
        GroesseAnwenden(_einstellungen.Groesse);

        // Position schon vor dem ersten Anzeigen setzen (kein Springen, kein Standardplatz von Windows).
        WindowStartupLocation = WindowStartupLocation.Manual;
        StartpositionSetzen();

        _symbol = SymbolErstellen();

        // ToDo
        _todos = new ObservableCollection<TodoEintrag>(Speicher.LadeTodos());
        foreach (var t in _todos) t.PropertyChanged += Todo_Geaendert;
        _todos.CollectionChanged += (_, _) => TodosGeaendert();
        _todoAnsicht = CollectionViewSource.GetDefaultView(_todos);
        _todoAnsicht.Filter = o => _einstellungen.ErledigteEinblenden || o is TodoEintrag { Erledigt: false };
        TodoListe.ItemsSource = _todoAnsicht;
        ErledigteEinblenden.IsChecked = _einstellungen.ErledigteEinblenden;

        // LAPS
        _verlauf = new ObservableCollection<string>(_einstellungen.LetzteRechner.Take(MaxVerlauf));
        _verlauf.CollectionChanged += (_, _) => VerlaufAnzeigen();
        LapsVerlauf.ItemsSource = _verlauf;

        // Eigene Tastatureingaben
        _eingaben = new ObservableCollection<Eingabe>(_einstellungen.Eingaben);
        foreach (var eg in _eingaben) eg.PropertyChanged += Eingabe_Geaendert;
        _eingaben.CollectionChanged += (_, _) =>
        {
            _einstellungen.Eingaben = _eingaben.ToList();
            EingabenLeer.Visibility = _eingaben.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Speicher.SpeichereEinstellungen(_einstellungen);
        };
        EingabenLeiste.ItemsSource = _eingaben;
        EingabenEditor.ItemsSource = _eingaben;
        EingabenLeer.Visibility = _eingaben.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _kennwortTimer.Tick += (_, _) => KennwortAusblenden();

        // Einstellungen
        GroesseRegler.Value = _einstellungen.Groesse;
        GroesseText.Text = $"{_einstellungen.Groesse:P0}";
        KuerzelListeFuellen();
        EinstLapsBenutzer.Text = _einstellungen.LapsBenutzer;
        EinstEnter.IsChecked = _einstellungen.EintippenMitEnter;
        EinstKiAdresse.Text = _einstellungen.KiAdresse;
        EinstBrowser.ItemsSource = Browser.Arten;
        EinstBrowser.SelectedItem = Browser.Arten.Contains(_einstellungen.KiBrowser) ? _einstellungen.KiBrowser : Browser.Standard;
        BrowserProfileLaden();
        EinstAutoUpdate.IsChecked = _einstellungen.AutoUpdate;
        VersionText.Text = $"Version {Aktualisierung.AktuellText}";

        TodosGeaendert(speichern: false);
        VerlaufAnzeigen();
        BereichZeigen(_einstellungen.Bereich);

        // Systemweites Tastenkürzel
        KuerzelAnwenden();
        _tastenkuerzel.Aufgenommen += (k, meldung) => Dispatcher.BeginInvoke(() => TasteAufgenommen(k, meldung));
        _tastenkuerzel.Ausgeloest += () => Dispatcher.BeginInvoke(() =>
        {
            if (_ausgeklappt && IsActive) Einklappen();
            else HervorholenUndAusklappen();
        });

        _sekundenTakt.Tick += (_, _) => Takt();
        _sekundenTakt.Start();

        Loaded += (_, _) =>
        {
            if (!IstAufEinemBildschirm()) PositionZuruecksetzen();
            if (App.NachUpdate)
                _symbol.ShowBalloonTip(5000, "Arbeitshelfer aktualisiert",
                    $"Jetzt auf Version {Aktualisierung.AktuellText}.", Forms.ToolTipIcon.Info);
            else if (_ersterStart)
                _symbol.ShowBalloonTip(6000, "Arbeitshelfer läuft",
                    $"Der blaue i-Knopf schwebt am rechten Bildschirmrand. {_tastenkuerzel.Kuerzel.Name} klappt ihn auf.",
                    Forms.ToolTipIcon.Info);
            _initialisiert = true;
        };
        Deactivated += (_, _) => ImVordergrundHalten();
        SizeChanged += (_, _) => AnkerHalten();
        PreviewKeyDown += Fenster_PreviewKeyDown;
        Closing += (_, _) => AllesSichern();
    }

    /// <summary>Laufende Zeit beenden und alles speichern (Beenden, Abmelden, Herunterfahren).</summary>
    private bool _gesichert;

    public void AllesSichern()
    {
        if (_gesichert) return;
        _gesichert = true;
        foreach (var t in _todos.Where(t => t.Laeuft)) t.Pausieren();
        Speicher.SpeichereTodos(_todos);
        Speicher.SpeichereEinstellungen(_einstellungen);
        SichereZwischenablage.Leeren();
        _tastenkuerzel.Dispose();
        _eintipper.Dispose();
        _symbol.Visible = false;
        _symbol.Dispose();
    }

    private void Takt()
    {
        var laufend = _todos.FirstOrDefault(t => t.Laeuft);
        laufend?.Aktualisieren();
        ZeitLaeuftPunkt.Visibility = laufend is null ? Visibility.Collapsed : Visibility.Visible;
        ZeitLaeuftPunkt.ToolTip = laufend is null ? null : $"Zeit läuft: {laufend.Text} ({laufend.SummeText})";
        _symbol.Text = laufend is null ? "Arbeitshelfer" : Kuerzen($"⏵ {laufend.Text}", 63);

        if (_ausgeklappt && BereichLaps.IsVisible)
            LapsZiel.Text = $"Eintippen in: {Kuerzen(_eintipper.ZielTitel(), 60)}";

        // Während eine Zeit läuft, minütlich speichern (Schutz bei Absturz/Herunterfahren).
        if (laufend is not null && ++_takte % 60 == 0) Speicher.SpeichereTodos(_todos);

        // Updates: beim Start und dann stündlich prüfen; installieren, sobald die App frei ist.
        if (_einstellungen.AutoUpdate && DateTime.Now >= _naechstePruefung)
        {
            _naechstePruefung = DateTime.Now.AddHours(1);
            _ = UpdatePruefenAsync(manuell: false);
        }
        if (_wartendesUpdate is UpdateInfo update && !_ausgeklappt && laufend is null && !_abfrageLaeuft)
            _ = UpdateInstallierenAsync(update);
    }

    // ───────────── Updates ─────────────

    private async Task UpdatePruefenAsync(bool manuell)
    {
        try
        {
            if (manuell) VersionText.Text = $"Version {Aktualisierung.AktuellText} – suche …";
            var update = await Aktualisierung.PruefenAsync();
            if (update is null)
            {
                VersionText.Text = $"Version {Aktualisierung.AktuellText} – aktuell";
                if (manuell)
                    _symbol.ShowBalloonTip(3000, "Arbeitshelfer", $"Version {Aktualisierung.AktuellText} ist aktuell.",
                        Forms.ToolTipIcon.Info);
                return;
            }
            VersionText.Text = $"Version {Aktualisierung.AktuellText} – {update.Version} verfügbar";
            if (manuell) await UpdateInstallierenAsync(update);
            else _wartendesUpdate = update;
        }
        catch (Exception ex)
        {
            App.FehlerProtokollieren(ex);
            VersionText.Text = $"Version {Aktualisierung.AktuellText} – Update-Prüfung fehlgeschlagen";
            if (manuell)
                _symbol.ShowBalloonTip(5000, "Update-Prüfung fehlgeschlagen", ex.Message, Forms.ToolTipIcon.Warning);
        }
    }

    private async Task UpdateInstallierenAsync(UpdateInfo update)
    {
        if (_updateLaeuft) return;
        _updateLaeuft = true;
        _wartendesUpdate = null;
        try
        {
            await Aktualisierung.InstallierenAsync(update);
            AllesSichern(); // vor dem Start der neuen Version, damit sie den aktuellen Stand liest
            ((App)Application.Current).Neustarten("--aktualisiert");
        }
        catch (Exception ex)
        {
            App.FehlerProtokollieren(ex);
            _naechstePruefung = DateTime.Now.AddHours(6); // nicht ständig erneut versuchen
            _symbol.ShowBalloonTip(8000, $"Update auf {update.Version} fehlgeschlagen", ex.Message, Forms.ToolTipIcon.Warning);
        }
        finally
        {
            _updateLaeuft = false;
        }
    }

    private void UpdateSuchen_Click(object sender, RoutedEventArgs e) => _ = UpdatePruefenAsync(manuell: true);

    private void EinstAutoUpdate_Click(object sender, RoutedEventArgs e)
    {
        _einstellungen.AutoUpdate = EinstAutoUpdate.IsChecked == true;
        Speicher.SpeichereEinstellungen(_einstellungen);
    }

    private static string Kuerzen(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ───────────── Tastatur ─────────────

    private void Fenster_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _ausgeklappt)
        {
            Einklappen();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        switch (e.Key)
        {
            case Key.D1 or Key.NumPad1: BereichWaehlen("ToDo"); break;
            case Key.D2 or Key.NumPad2: KiOeffnen(); break;
            case Key.D3 or Key.NumPad3: BereichWaehlen("Laps"); break;
            case Key.D0 or Key.NumPad0: BereichWaehlen("Einstellungen"); break;
            default: return;
        }
        e.Handled = true;
    }

    // ───────────── Größe ─────────────

    private void GroesseAnwenden(double faktor)
    {
        Gesamt.LayoutTransform = new ScaleTransform(faktor, faktor);
    }

    private void GroesseRegler_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GroesseText is null) return; // während InitializeComponent
        var faktor = Math.Round(e.NewValue, 2);
        GroesseText.Text = $"{faktor:P0}";
        if (!_initialisiert) return;
        _einstellungen.Groesse = faktor;
        KnopfFesthalten(() => GroesseAnwenden(faktor));
        if (_ausgeklappt) RichtungPruefen();
        Speicher.SpeichereEinstellungen(_einstellungen);
    }

    // ───────────── Position, Auf-/Zuklappen ─────────────

    private double Faktor => _einstellungen.Groesse;

    private void StartpositionSetzen()
    {
        if (_einstellungen.Links is double l && _einstellungen.Oben is double o)
        {
            Left = l - Gesamt.Margin.Left;
            Top = o - Gesamt.Margin.Top;
        }
        else
        {
            StandardpositionSetzen();
        }
    }

    /// <summary>Rechter Bildschirmrand, etwas unterhalb der Mitte, auf dem Hauptbildschirm.</summary>
    private void StandardpositionSetzen()
    {
        var arbeit = SystemParameters.WorkArea;
        Left = arbeit.Right - InfoKnopf.Width * Faktor - Gesamt.Margin.Left - 16;
        Top = arbeit.Top + arbeit.Height * 0.6;
    }

    private double DpiSkala()
    {
        try { return VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { return 1; }
    }

    /// <summary>Liegt der Knopf vollständig auf einem der aktuell angeschlossenen Bildschirme?</summary>
    private bool IstAufEinemBildschirm()
    {
        var skala = DpiSkala();
        var p = KnopfVersatz();
        var groesse = InfoKnopf.Width * Faktor;
        var knopf = new System.Drawing.Rectangle(
            (int)((Left + p.X) * skala), (int)((Top + p.Y) * skala),
            (int)(groesse * skala), (int)(groesse * skala));
        return Forms.Screen.AllScreens.Any(s => s.WorkingArea.Contains(knopf));
    }

    /// <summary>Arbeitsbereich (ohne Taskleiste) des Bildschirms, auf dem der Knopf liegt – in WPF-Einheiten.</summary>
    private Rect BildschirmDesKnopfes()
    {
        var skala = DpiSkala();
        var p = KnopfVersatz();
        var bildschirm = Forms.Screen.FromPoint(new System.Drawing.Point(
            (int)((Left + p.X) * skala), (int)((Top + p.Y) * skala)));
        var w = bildschirm.WorkingArea;
        return new Rect(w.Left / skala, w.Top / skala, w.Width / skala, w.Height / skala);
    }

    private void PositionZuruecksetzen()
    {
        Einklappen();
        StandardpositionSetzen();
        PositionMerken();
    }

    /// <summary>Für Infobereich-Symbol, Win+Q und zweiten Programmstart: Knopf sicher sichtbar machen.</summary>
    public void HervorholenUndAusklappen()
    {
        if (!IsVisible) Show();
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        if (!IstAufEinemBildschirm()) PositionZuruecksetzen();
        ImVordergrundHalten();
        Ausklappen();
        Activate();
        FokusSetzen();
    }

    private void EinfacheDarstellungAnwenden()
    {
        // Ohne AllowsTransparency: kein Layered Window, funktioniert auch per RDP/Citrix und mit schwachen Grafiktreibern.
        AllowsTransparency = false;
        Background = (Brush)FindResource("Flaeche");
        InfoKnopf.CornerRadius = new CornerRadius(6);
        InfoKnopf.Effect = null;
        Leiste.Effect = null;
    }

    private Point KnopfVersatz() => InfoKnopf.TranslatePoint(new Point(0, 0), this);

    /// <summary>Führt eine Layoutänderung aus, ohne dass der Knopf auf dem Bildschirm springt.</summary>
    private void KnopfFesthalten(Action aenderung)
    {
        _positioniere = true;
        var vorher = KnopfVersatz();
        aenderung();
        UpdateLayout();
        var nachher = KnopfVersatz();
        Left += vorher.X - nachher.X;
        Top += vorher.Y - nachher.Y;
        _ankerUnten = Top + ActualHeight;
        _positioniere = false;
    }

    private void Ausklappen()
    {
        if (_ausgeklappt) return;
        _ausgeklappt = true;
        KnopfFesthalten(() => Leiste.Visibility = Visibility.Visible);
        RichtungPruefen();
        Activate();
        FokusSetzen();
    }

    /// <summary>Leiste nach rechts/unten öffnen; wenn das nicht auf den Bildschirm passt, nach links/oben.</summary>
    private void RichtungPruefen()
    {
        KnopfFesthalten(() => LayoutRichtung(rechtsbuendig: false, nachOben: false));
        var bereich = BildschirmDesKnopfes();
        var rechtsbuendig = Left + ActualWidth > bereich.Right;
        var nachOben = Top + ActualHeight > bereich.Bottom;
        if (rechtsbuendig || nachOben)
            KnopfFesthalten(() => LayoutRichtung(rechtsbuendig, nachOben));
    }

    private void Einklappen()
    {
        if (!_ausgeklappt) return;
        _ausgeklappt = false;
        KnopfFesthalten(() =>
        {
            Leiste.Visibility = Visibility.Collapsed;
            LayoutRichtung(rechtsbuendig: false, nachOben: false);
        });
    }

    private void LayoutRichtung(bool rechtsbuendig, bool nachOben)
    {
        _nachOben = nachOben;
        var ausrichtung = rechtsbuendig ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        InfoKnopf.HorizontalAlignment = ausrichtung;
        Leiste.HorizontalAlignment = ausrichtung;
        Grid.SetRow(InfoKnopf, nachOben ? 1 : 0);
        Grid.SetRow(Leiste, nachOben ? 0 : 1);
        Leiste.Margin = nachOben ? new Thickness(0, 0, 0, 6) : new Thickness(0, 6, 0, 0);
    }

    private void AnkerHalten()
    {
        if (_positioniere || !_ausgeklappt || !_nachOben) return;
        Top = _ankerUnten - ActualHeight;
    }

    private void ImVordergrundHalten()
    {
        // Manche Anwendungen schieben sich trotz Topmost davor – kurz neu setzen.
        Topmost = false;
        Topmost = true;
    }

    private void PositionMerken()
    {
        var p = KnopfVersatz();
        _einstellungen.Links = Left + p.X;
        _einstellungen.Oben = Top + p.Y;
        _ankerUnten = Top + ActualHeight;
        Speicher.SpeichereEinstellungen(_einstellungen);
    }

    // ───────────── Info-Knopf: Klick vs. Ziehen ─────────────

    private void InfoKnopf_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _druckPunkt = e.GetPosition(this);
        InfoKnopf.CaptureMouse();
        e.Handled = true;
    }

    private void InfoKnopf_MouseMove(object sender, MouseEventArgs e)
    {
        if (_druckPunkt is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        var jetzt = e.GetPosition(this);
        if (Math.Abs(jetzt.X - start.X) < 4 && Math.Abs(jetzt.Y - start.Y) < 4) return;

        _druckPunkt = null;
        InfoKnopf.ReleaseMouseCapture();
        DragMove();
        PositionMerken();
    }

    private void InfoKnopf_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        InfoKnopf.ReleaseMouseCapture();
        if (_druckPunkt is null) return; // war ein Ziehen
        _druckPunkt = null;
        if (_ausgeklappt) Einklappen(); else Ausklappen();
        e.Handled = true;
    }

    private void Einklappen_Click(object sender, RoutedEventArgs e) => Einklappen();

    // ───────────── Infobereich-Symbol & Kontextmenü ─────────────

    private static System.Drawing.Icon SymbolLaden()
    {
        using var datei = typeof(MainWindow).Assembly.GetManifestResourceStream("Arbeitshelfer.info.ico");
        return datei is null
            ? System.Drawing.SystemIcons.Information
            : new System.Drawing.Icon(datei, Forms.SystemInformation.SmallIconSize);
    }

    private Forms.NotifyIcon SymbolErstellen()
    {
        var menue = new Forms.ContextMenuStrip();
        menue.Items.Add("Knopf anzeigen", null, (_, _) => HervorholenUndAusklappen());
        menue.Items.Add("Einstellungen", null, (_, _) => { HervorholenUndAusklappen(); BereichWaehlen("Einstellungen"); });
        menue.Items.Add("Position zurücksetzen", null, (_, _) => { PositionZuruecksetzen(); ImVordergrundHalten(); });
        var einfach = new Forms.ToolStripMenuItem("Einfache Darstellung (bei Remote-Desktop)")
        {
            Checked = _einstellungen.EinfacheDarstellung,
            ToolTipText = "Falls der Knopf unsichtbar bleibt. Startet die App neu.",
        };
        einfach.Click += (_, _) =>
        {
            _einstellungen.EinfacheDarstellung = !_einstellungen.EinfacheDarstellung;
            AllesSichern();
            ((App)Application.Current).Neustarten();
        };
        menue.Items.Add(einfach);
        menue.Items.Add("Nach Updates suchen", null, (_, _) => _ = UpdatePruefenAsync(manuell: true));
        menue.Items.Add("Einstellungsordner öffnen", null, (_, _) => OrdnerOeffnen());
        menue.Items.Add(new Forms.ToolStripSeparator());
        menue.Items.Add("Beenden", null, (_, _) => Close());

        var symbol = new Forms.NotifyIcon
        {
            Icon = SymbolLaden(),
            Text = "Arbeitshelfer",
            ContextMenuStrip = menue,
            Visible = true,
        };
        symbol.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) HervorholenUndAusklappen();
        };
        return symbol;
    }

    private void MenuEinstellungen_Click(object sender, RoutedEventArgs e)
    {
        Ausklappen();
        BereichWaehlen("Einstellungen");
    }

    private void MenuOrdner_Click(object sender, RoutedEventArgs e) => OrdnerOeffnen();

    private void MenuBeenden_Click(object sender, RoutedEventArgs e) => Close();

    private static void OrdnerOeffnen()
    {
        Directory.CreateDirectory(App.Ordner);
        Process.Start(new ProcessStartInfo(App.Ordner) { UseShellExecute = true });
    }

    // ───────────── Symbolleiste ─────────────

    private void BereichZeigen(string bereich)
    {
        _einstellungen.Bereich = bereich is "ToDo" or "Einstellungen" ? bereich : "Laps";
        var b = _einstellungen.Bereich;
        BereichTodo.Visibility = b == "ToDo" ? Visibility.Visible : Visibility.Collapsed;
        BereichLaps.Visibility = b == "Laps" ? Visibility.Visible : Visibility.Collapsed;
        BereichEinstellungen.Visibility = b == "Einstellungen" ? Visibility.Visible : Visibility.Collapsed;
        var aktiv = new SolidColorBrush(Color.FromRgb(0xDD, 0xE7, 0xF7));
        BtnTodo.Background = b == "ToDo" ? aktiv : Brushes.Transparent;
        BtnLaps.Background = b == "Laps" ? aktiv : Brushes.Transparent;
        BtnEinstellungen.Background = b == "Einstellungen" ? aktiv : Brushes.Transparent;
        if (b == "Einstellungen") EinstAutostart.IsChecked = AutostartAktiv();
    }

    private void BereichWaehlen(string bereich)
    {
        Ausklappen();
        BereichZeigen(bereich);
        FokusSetzen();
        Speicher.SpeichereEinstellungen(_einstellungen);
    }

    private void FokusSetzen()
    {
        Control? ziel = _einstellungen.Bereich switch
        {
            "ToDo" => TodoEingabe,
            "Laps" => LapsEingabe,
            _ => null,
        };
        if (ziel is not null)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { ziel.Focus(); Keyboard.Focus(ziel); });
    }

    private void BtnTodo_Click(object sender, RoutedEventArgs e) => BereichWaehlen("ToDo");
    private void BtnLaps_Click(object sender, RoutedEventArgs e) => BereichWaehlen("Laps");
    private void BtnEinstellungen_Click(object sender, RoutedEventArgs e) => BereichWaehlen("Einstellungen");
    private void BtnKi_Click(object sender, RoutedEventArgs e) => KiOeffnen();

    private void KiOeffnen()
    {
        if (string.IsNullOrWhiteSpace(_einstellungen.KiAdresse))
        {
            // Noch keine Adresse hinterlegt: direkt zum Feld in den Einstellungen.
            BereichWaehlen("Einstellungen");
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { EinstKiAdresse.Focus(); EinstKiAdresse.BringIntoView(); });
            return;
        }
        try
        {
            Browser.Oeffnen(_einstellungen.KiAdresse, _einstellungen.KiBrowser, _einstellungen.KiProfil);
            Einklappen();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Browser konnte nicht geöffnet werden:\n{ex.Message}", "Arbeitshelfer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ───────────── ToDo & Zeiterfassung ─────────────

    private void Todo_Geaendert(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TodoEintrag.Erledigt))
        {
            Speicher.SpeichereTodos(_todos);
            // Nach dem Klick (Häkchen sichtbar) aus der Liste nehmen.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { _todoAnsicht.Refresh(); TodoLeerAnzeigen(); });
        }
        else if (e.PropertyName == nameof(TodoEintrag.Text))
        {
            Speicher.SpeichereTodos(_todos);
        }
    }

    private void TodosGeaendert(bool speichern = true)
    {
        TodoLeerAnzeigen();
        if (speichern) Speicher.SpeichereTodos(_todos);
    }

    private void TodoLeerAnzeigen()
    {
        var sichtbar = _todos.Count(t => _einstellungen.ErledigteEinblenden || !t.Erledigt);
        TodoLeer.Visibility = sichtbar == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TodoHinzufuegen(bool zeitStarten)
    {
        var text = TodoEingabe.Text.Trim();
        if (text.Length == 0) return;
        var eintrag = new TodoEintrag { Text = text };
        eintrag.PropertyChanged += Todo_Geaendert;
        _todos.Insert(0, eintrag);
        TodoEingabe.Clear();
        if (zeitStarten) ZeitStarten(eintrag);
    }

    private void TodoEingabe_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        TodoHinzufuegen(zeitStarten: Keyboard.Modifiers == ModifierKeys.Shift);
        e.Handled = true;
    }

    private void TodoHinzufuegen_Click(object sender, RoutedEventArgs e) => TodoHinzufuegen(zeitStarten: false);

    private void ZeitStarten(TodoEintrag eintrag)
    {
        foreach (var t in _todos.Where(t => t != eintrag && t.Laeuft)) t.Pausieren(); // immer nur eine Uhr
        eintrag.Starten();
        Speicher.SpeichereTodos(_todos);
        Takt();
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TodoEintrag eintrag) return;
        if (eintrag.Laeuft)
        {
            eintrag.Pausieren();
            Speicher.SpeichereTodos(_todos);
            Takt();
        }
        else
        {
            ZeitStarten(eintrag);
        }
    }

    private void SummeKlick_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TodoEintrag eintrag)
            eintrag.BuchungenOffen = !eintrag.BuchungenOffen;
    }

    private void ErledigteEinblenden_Click(object sender, RoutedEventArgs e)
    {
        _einstellungen.ErledigteEinblenden = ErledigteEinblenden.IsChecked == true;
        _todoAnsicht.Refresh();
        TodoLeerAnzeigen();
        Speicher.SpeichereEinstellungen(_einstellungen);
    }

    // ───────────── LAPS ─────────────

    private void VerlaufAnzeigen()
    {
        LapsVerlaufLeer.Visibility = _verlauf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LapsEingabe_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _ = LapsAbfragenAsync(LapsEingabe.Text, eintippen: Keyboard.Modifiers == ModifierKeys.Shift);
        e.Handled = true;
    }

    private void LapsAbfragen_Click(object sender, RoutedEventArgs e) => _ = LapsAbfragenAsync(LapsEingabe.Text, eintippen: false);

    private void LapsVerlauf_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string rechner) _ = LapsAbfragenAsync(rechner, eintippen: true);
    }

    private void LapsVerlaufAnzeigen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string rechner) _ = LapsAbfragenAsync(rechner, eintippen: false);
    }

    private async Task LapsAbfragenAsync(string eingabe, bool eintippen)
    {
        var rechner = eingabe.Trim().ToUpperInvariant();
        if (_abfrageLaeuft || rechner.Length == 0) return;
        if (!LapsAbfrage.IstGueltig(rechner))
        {
            Status($"„{eingabe.Trim()}“ ist kein gültiger Rechnername.", fehler: true);
            return;
        }

        _abfrageLaeuft = true;
        LapsAbfragenKnopf.IsEnabled = false;
        LapsVerlauf.IsEnabled = false;
        KennwortAusblenden();
        Status($"Frage {rechner} ab …", fehler: false);
        try
        {
            var ergebnis = await LapsAbfrage.AbfragenAsync(rechner);
            ImVerlaufMerken(rechner);
            LapsEingabe.Clear();

            if (eintippen)
            {
                Status("", fehler: false);
                await EintippenAsync(rechner, _einstellungen.LapsBenutzer, ergebnis.Kennwort, _einstellungen.EintippenMitEnter);
                return;
            }

            KennwortAnzeigen(rechner, ergebnis);
            var kopiert = KennwortKopieren();

            var text = kopiert ? "In die Zwischenablage kopiert." : "Kopieren fehlgeschlagen – bitte abtippen.";
            if (kopiert && _einstellungen.ZwischenablageLeerenNachSekunden > 0)
                text += $" Wird nach {_einstellungen.ZwischenablageLeerenNachSekunden} s gelöscht.";
            if (ergebnis.Ablauf is DateTime ablauf)
                text += $"\nGültig bis {ablauf:dd.MM.yyyy HH:mm}.";
            Status(text, fehler: !kopiert);
        }
        catch (LapsFehler ex)
        {
            Status(ex.Message, fehler: true);
        }
        catch (Exception ex)
        {
            App.FehlerProtokollieren(ex);
            Status($"Unerwarteter Fehler: {ex.Message}", fehler: true);
        }
        finally
        {
            _abfrageLaeuft = false;
            LapsAbfragenKnopf.IsEnabled = true;
            LapsVerlauf.IsEnabled = true;
        }
    }

    /// <summary>Auto-Type: ins zuletzt aktive Fenster wechseln, Benutzer TAB Kennwort (ENTER) tippen.</summary>
    private async Task EintippenAsync(string bezeichnung, string benutzer, string kennwort, bool mitEnter)
    {
        var titel = _eintipper.ZielTitel();
        Einklappen();
        try
        {
            await _eintipper.EintippenAsync(benutzer, kennwort, mitEnter);
            Status($"{bezeichnung}: eingetippt in „{Kuerzen(titel, 60)}“.", fehler: false);
        }
        catch (Exception ex)
        {
            Status($"Eintippen fehlgeschlagen: {ex.Message}", fehler: true);
            _symbol.ShowBalloonTip(5000, "Eintippen fehlgeschlagen", ex.Message, Forms.ToolTipIcon.Warning);
        }
    }

    private void KennwortEintippen_Click(object sender, RoutedEventArgs e)
    {
        if (_angezeigtesKennwort is string kennwort)
            _ = EintippenAsync(_kennwortRechner ?? "", _einstellungen.LapsBenutzer, kennwort, _einstellungen.EintippenMitEnter);
    }

    private string? _kennwortRechner;

    // ───────────── Eigene Tastatureingaben ─────────────

    private void Eingabe_Geaendert(object? sender, PropertyChangedEventArgs e) =>
        Speicher.SpeichereEinstellungen(_einstellungen);

    private void EingabeTippen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Eingabe eg) return;
        if (eg.Kennwort.Length == 0 && eg.Benutzer.Length == 0)
        {
            Status($"„{eg.Name}“ ist leer – unter ⚙ Einstellungen ausfüllen.", fehler: true);
            return;
        }
        _ = EintippenAsync(eg.Name, eg.Benutzer, eg.Kennwort, eg.MitEnter);
    }

    private void EingabeNeu_Click(object sender, RoutedEventArgs e)
    {
        var eg = new Eingabe { Name = "Neue Eingabe", Symbol = "Balken" };
        eg.PropertyChanged += Eingabe_Geaendert;
        _eingaben.Add(eg);
    }

    private void EingabeLoeschen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Eingabe eg) return;
        if (MessageBox.Show(this, $"„{eg.Name}“ löschen?", "Arbeitshelfer", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        eg.PropertyChanged -= Eingabe_Geaendert;
        _eingaben.Remove(eg);
    }

    private void EingabeSymbolDatei_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Eingabe eg) return;
        var dialog = new OpenFileDialog
        {
            Title = "Symbol wählen",
            Filter = "Programme und Symbole|*.exe;*.ico;*.lnk;*.png;*.jpg;*.bmp|Alle Dateien|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            eg.Symbol = Symbole.DateiPraefix + dialog.FileName;
    }

    private void EingabeKennwort_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.Tag is Eingabe eg && box.Password != eg.Kennwort)
            box.Password = eg.Kennwort;
    }

    private void EingabeKennwort_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.Tag is Eingabe eg && box.Password != eg.Kennwort)
            eg.Kennwort = box.Password;
    }

    private void KennwortAnzeigen(string rechner, LapsErgebnis ergebnis)
    {
        _kennwortRechner = rechner;
        _angezeigtesKennwort = ergebnis.Kennwort;
        KennwortRechner.Text = $"{rechner}  ·  blendet sich nach {_einstellungen.KennwortAnzeigenSekunden} s aus";
        KennwortText.Text = ergebnis.Kennwort;
        KennwortKasten.Visibility = Visibility.Visible;
        _kennwortTimer.Stop();
        _kennwortTimer.Interval = TimeSpan.FromSeconds(Math.Max(5, _einstellungen.KennwortAnzeigenSekunden));
        _kennwortTimer.Start();
    }

    private void KennwortAusblenden()
    {
        _kennwortTimer.Stop();
        _angezeigtesKennwort = null;
        KennwortText.Text = "";
        KennwortKasten.Visibility = Visibility.Collapsed;
    }

    private bool KennwortKopieren()
    {
        if (_angezeigtesKennwort is null) return false;
        try
        {
            SichereZwischenablage.Kopieren(_angezeigtesKennwort, _einstellungen.ZwischenablageLeerenNachSekunden);
            return true;
        }
        catch (Exception ex)
        {
            App.FehlerProtokollieren(ex);
            return false;
        }
    }

    private void KennwortKopieren_Click(object sender, RoutedEventArgs e)
    {
        if (KennwortKopieren()) Status("Erneut in die Zwischenablage kopiert.", fehler: false);
    }

    private void KennwortAusblenden_Click(object sender, RoutedEventArgs e) => KennwortAusblenden();

    private void ImVerlaufMerken(string rechner)
    {
        var vorhanden = _verlauf.IndexOf(rechner);
        if (vorhanden == 0) return;
        if (vorhanden > 0) _verlauf.RemoveAt(vorhanden);
        _verlauf.Insert(0, rechner);
        while (_verlauf.Count > MaxVerlauf) _verlauf.RemoveAt(_verlauf.Count - 1);
        _einstellungen.LetzteRechner = _verlauf.ToList();
        Speicher.SpeichereEinstellungen(_einstellungen);
    }

    private void Status(string text, bool fehler)
    {
        LapsStatus.Text = text;
        LapsStatus.Foreground = fehler
            ? new SolidColorBrush(Color.FromRgb(0xCF, 0x22, 0x2E))
            : (Brush)FindResource("TextGedimmt");
    }

    // ───────────── Einstellungen ─────────────

    private static bool AutostartAktiv()
    {
        using var k = Registry.CurrentUser.OpenSubKey(AutostartSchluessel);
        return k?.GetValue(AutostartName) is string;
    }

    private void EinstAutostart_Click(object sender, RoutedEventArgs e)
    {
        using var k = Registry.CurrentUser.CreateSubKey(AutostartSchluessel);
        if (EinstAutostart.IsChecked == true && Environment.ProcessPath is string pfad)
            k.SetValue(AutostartName, $"\"{pfad}\"");
        else
            k.DeleteValue(AutostartName, throwOnMissingValue: false);
    }

    private const string EigenPraefix = "Eigene Taste: ";

    private Kuerzel? EigenesKuerzel() =>
        _einstellungen.EigeneTasteVk == 0 || _einstellungen.EigeneTasteName is null
            ? null
            : new Kuerzel(_einstellungen.EigeneTasteName, (Modifikator)_einstellungen.EigeneTasteMods,
                _einstellungen.EigeneTasteVk, "selbst aufgenommen");

    private Kuerzel AktivesKuerzel() =>
        _einstellungen.Tastenkuerzel == "Eigen" && EigenesKuerzel() is Kuerzel eigen
            ? eigen
            : Tastenkuerzel.Finden(_einstellungen.Tastenkuerzel);

    private void KuerzelListeFuellen()
    {
        var namen = Tastenkuerzel.Auswahl.Select(k => k.Name).ToList();
        if (EigenesKuerzel() is Kuerzel eigen) namen.Insert(0, EigenPraefix + eigen.Name);
        EinstKuerzel.ItemsSource = namen;
        var aktiv = AktivesKuerzel();
        EinstKuerzel.SelectedItem = _einstellungen.Tastenkuerzel == "Eigen" ? EigenPraefix + aktiv.Name : aktiv.Name;
    }

    private void TasteAufnehmen_Click(object sender, RoutedEventArgs e)
    {
        _tastenkuerzel.Aufnahme = true;
        TasteAufnehmenKnopf.Content = "Jetzt Taste drücken …";
        EinstKuerzelHinweis.Text = "Gewünschte Taste drücken (gern mit Strg/Alt/Umschalt/Win). Esc bricht ab.";
    }

    private void TasteAufgenommen(Kuerzel? k, string? meldung)
    {
        TasteAufnehmenKnopf.Content = "Taste aufnehmen";
        if (k is null)
        {
            EinstKuerzelHinweis.Text = meldung ?? "";
            return;
        }
        _einstellungen.EigeneTasteName = k.Name;
        _einstellungen.EigeneTasteVk = k.Vk;
        _einstellungen.EigeneTasteMods = (int)k.Mods;
        _einstellungen.Tastenkuerzel = "Eigen";
        Speicher.SpeichereEinstellungen(_einstellungen);
        var war = _initialisiert;
        _initialisiert = false; // SelectionChanged beim Neufüllen ignorieren
        KuerzelListeFuellen();
        _initialisiert = war;
        KuerzelAnwenden();
        EinstKuerzelHinweis.Text = $"Übernommen: {k.Name}";
    }

    private void KuerzelAnwenden()
    {
        var k = AktivesKuerzel();
        _tastenkuerzel.Kuerzel = k;
        EinstKuerzelHinweis.Text = k.Hinweis;
        InfoKnopf.ToolTip = k.Vk == 0
            ? "Klick: auf-/zuklappen · Ziehen: verschieben · Rechtsklick: Menü"
            : $"Klick oder {k.Name}: auf-/zuklappen · Ziehen: verschieben · Rechtsklick: Menü";
    }

    private void EinstKuerzel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EinstKuerzel.SelectedItem is not string name || !_initialisiert) return;
        _einstellungen.Tastenkuerzel = name.StartsWith(EigenPraefix) ? "Eigen" : name;
        KuerzelAnwenden();
        Speicher.SpeichereEinstellungen(_einstellungen);
    }

    private void EinstLapsBenutzer_LostFocus(object sender, RoutedEventArgs e)
    {
        _einstellungen.LapsBenutzer = EinstLapsBenutzer.Text.Trim();
        Speicher.SpeichereEinstellungen(_einstellungen);
    }

    private void EinstEnter_Click(object sender, RoutedEventArgs e)
    {
        _einstellungen.EintippenMitEnter = EinstEnter.IsChecked == true;
        Speicher.SpeichereEinstellungen(_einstellungen);
    }

    private void BrowserProfileLaden()
    {
        var art = EinstBrowser.SelectedItem as string ?? Browser.Standard;
        var profile = Browser.Profile(art);
        EinstProfil.ItemsSource = profile;
        EinstProfil.SelectedValue = _einstellungen.KiProfil;
        if (EinstProfil.SelectedValue is null && art != Browser.Standard && profile.Count > 0)
            EinstProfil.SelectedIndex = 0;
        var mitProfil = art != Browser.Standard;
        EinstProfilBereich.Visibility = mitProfil ? Visibility.Visible : Visibility.Collapsed;
        EinstProfilHinweis.Text = mitProfil && profile.Count == 0
            ? $"Keine {art}-Profile gefunden – es wird das zuletzt benutzte Profil verwendet."
            : "Die KI öffnet immer in diesem Profil, egal in welchem du gerade bist.";
    }

    private void EinstBrowser_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialisiert || EinstBrowser.SelectedItem is not string art) return;
        _einstellungen.KiBrowser = art;
        _einstellungen.KiProfil = null;
        BrowserProfileLaden();
        _einstellungen.KiProfil = EinstProfil.SelectedValue as string;
        Speicher.SpeichereEinstellungen(_einstellungen);
    }

    private void EinstProfil_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialisiert) return;
        _einstellungen.KiProfil = EinstProfil.SelectedValue as string;
        Speicher.SpeichereEinstellungen(_einstellungen);
    }

    private void KiTesten_Click(object sender, RoutedEventArgs e) => KiOeffnen();

    private void EinstKiAdresse_LostFocus(object sender, RoutedEventArgs e)
    {
        var adresse = EinstKiAdresse.Text.Trim();
        if (Uri.TryCreate(adresse, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            _einstellungen.KiAdresse = adresse;
            Speicher.SpeichereEinstellungen(_einstellungen);
        }
        else
        {
            EinstKiAdresse.Text = _einstellungen.KiAdresse;
        }
    }
}
