using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Arbeitshelfer;

/// <summary>Eingebaute Symbole für eigene Tastatureingaben, plus Symbole aus Dateien (.exe, .ico, .png).</summary>
public static class Symbole
{
    public const string DateiPraefix = "datei:";

    /// <summary>Auswahl in den Einstellungen (Anzeigename = Schlüssel).</summary>
    public static readonly string[] Eingebaut =
    {
        "Balken", "Balken mit T", "Tastatur", "Schlüssel", "Bildschirm", "Globus", "Person", "Stern",
    };

    private static readonly Dictionary<string, char> Glyphen = new()
    {
        ["Tastatur"] = '',
        ["Schlüssel"] = '',
        ["Bildschirm"] = '',
        ["Globus"] = '',
        ["Person"] = '',
        ["Stern"] = '',
    };

    private static readonly Dictionary<string, ImageSource> Zwischenspeicher = new();

    public static ImageSource Bild(string? symbol)
    {
        symbol ??= "Tastatur";
        if (Zwischenspeicher.TryGetValue(symbol, out var bild)) return bild;
        bild = Erzeugen(symbol) ?? Erzeugen("Tastatur")!;
        bild.Freeze();
        Zwischenspeicher[symbol] = bild;
        return bild;
    }

    private static ImageSource? Erzeugen(string symbol)
    {
        try
        {
            if (symbol == "Balken") return Ressource("balken.png");
            if (symbol == "Balken mit T") return Ressource("balken_t.png");
            if (Glyphen.TryGetValue(symbol, out var glyphe)) return Glyphe(glyphe);
            if (symbol.StartsWith(DateiPraefix)) return AusDatei(symbol[DateiPraefix.Length..]);
        }
        catch (Exception ex)
        {
            App.FehlerProtokollieren(ex);
        }
        return null;
    }

    private static ImageSource Ressource(string name) =>
        new BitmapImage(new Uri($"pack://application:,,,/Symbole/{name}"));

    private static ImageSource Glyphe(char zeichen)
    {
        var text = new FormattedText(zeichen.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), 32, Brushes.Black, 1.0);
        var geometrie = text.BuildGeometry(new Point(0, 0));
        var zeichnung = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0x24, 0x29, 0x2F)), null, geometrie);
        return new DrawingImage(zeichnung);
    }

    private static ImageSource? AusDatei(string pfad)
    {
        if (!File.Exists(pfad)) return null;
        var endung = Path.GetExtension(pfad).ToLowerInvariant();
        if (endung is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad; // Datei nicht sperren
            b.UriSource = new Uri(pfad);
            b.DecodePixelWidth = 64;
            b.EndInit();
            return b;
        }
        // .exe, .ico, .lnk, … – das Symbol, das auch der Explorer zeigt
        using var icon = System.Drawing.Icon.ExtractAssociatedIcon(pfad);
        if (icon is null) return null;
        return Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    }
}
