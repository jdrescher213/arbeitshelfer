using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Arbeitshelfer;

[Flags]
public enum Modifikator { Keine = 0, Win = 1, Strg = 2, Alt = 4, Umschalt = 8 }

public sealed record Kuerzel(string Name, Modifikator Mods, int Vk, string Hinweis);

/// <summary>
/// Fängt das gewählte Tastenkürzel systemweit ab. Win+Q ist von Windows (Suche) belegt und lässt
/// sich nicht per RegisterHotKey registrieren, daher ein Low-Level-Tastaturhook (wie bei PowerToys).
/// Damit beim Loslassen der Win-Taste nicht das Startmenü aufgeht, wird eine
/// unbelegte Taste (VK 0xE8) eingeschoben.
/// </summary>
public sealed class Tastenkuerzel : IDisposable
{
    /// <summary>Auswahl in den Einstellungen. Strg+Alt+Buchstabe nur, wo AltGr+Buchstabe auf deutscher Tastatur nichts tippt.</summary>
    public static readonly Kuerzel[] Auswahl =
    {
        new("Win+Q", Modifikator.Win, 0x51, "ersetzt die Windows-Suche (Win+S geht weiterhin)"),
        new("Strg+Alt+I", Modifikator.Strg | Modifikator.Alt, 0x49, "„I“ wie Info"),
        new("Strg+Alt+A", Modifikator.Strg | Modifikator.Alt, 0x41, "„A“ wie Arbeitshelfer"),
        new("Strg+Umschalt+Leertaste", Modifikator.Strg | Modifikator.Umschalt, 0x20, "in Word: geschütztes Leerzeichen"),
        new("Rollen-Taste", Modifikator.Keine, 0x91, "eine Taste, wird sonst kaum genutzt"),
        new("Pause-Taste", Modifikator.Keine, 0x13, "eine Taste, wird sonst kaum genutzt"),
        new("Aus", Modifikator.Keine, 0, "nur per Klick auf den Knopf"),
    };

    public static Kuerzel Finden(string? name) =>
        Auswahl.FirstOrDefault(k => k.Name == name) ?? Auswahl[0];

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int LLKHF_INJECTED = 0x10;
    private const int LLKHF_EXTENDED = 0x01;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const ushort VK_UNBELEGT = 0xE8;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private readonly HookProc _proc;
    private IntPtr _hook;

    public event Action? Ausgeloest;

    /// <summary>Aufnahme einer eigenen Taste: liefert das Kürzel, oder null + Meldung (abgebrochen/ungeeignet).</summary>
    public event Action<Kuerzel?, string?>? Aufgenommen;

    /// <summary>Solange true, wird die nächste Taste aufgenommen statt ausgewertet.</summary>
    public bool Aufnahme { get; set; }
    public Kuerzel Kuerzel { get; set; } = Auswahl[0];
    public bool Installiert => _hook != IntPtr.Zero;

    public Tastenkuerzel()
    {
        _proc = Rueckruf; // Referenz halten, sonst räumt der GC den Delegaten weg.
        using var modul = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(modul?.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
    }

    private IntPtr Rueckruf(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Aufnahme && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLKHF_INJECTED) == 0 && !IstModifikatorTaste(info.vkCode))
            {
                var mods = AktuelleModifikatoren();
                Aufnahme = false;
                if (info.vkCode == VK_ESCAPE && mods == Modifikator.Keine)
                {
                    Aufgenommen?.Invoke(null, "Abgebrochen.");
                }
                else if (mods == Modifikator.Keine && IstSchreibtaste(info.vkCode))
                {
                    Aufgenommen?.Invoke(null,
                        "Diese Taste braucht man zum Schreiben – bitte mit Strg, Alt, Umschalt oder Win kombinieren.");
                }
                else
                {
                    if (mods.HasFlag(Modifikator.Win) || mods.HasFlag(Modifikator.Alt)) UnbelegteTasteSenden();
                    var name = ModifikatorText(mods) + TastenName(info.vkCode, info.scanCode, (info.flags & LLKHF_EXTENDED) != 0);
                    Aufgenommen?.Invoke(new Kuerzel(name, mods, info.vkCode, "eigene Taste"), null);
                }
                return 1; // die aufgenommene Taste löst sonst nichts aus
            }
        }

        var k = Kuerzel;
        if (nCode >= 0 && k.Vk != 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (info.vkCode == k.Vk && (info.flags & LLKHF_INJECTED) == 0 && AktuelleModifikatoren() == k.Mods)
            {
                if (k.Mods.HasFlag(Modifikator.Win) || k.Mods.HasFlag(Modifikator.Alt))
                    UnbelegteTasteSenden(); // sonst öffnet das Loslassen Startmenü bzw. Menüleiste
                Ausgeloest?.Invoke();
                return 1; // Die Tastenkombination geht nicht an andere Programme.
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IstModifikatorTaste(int vk) =>
        vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_LWIN or VK_RWIN or >= 0xA0 and <= 0xA5 or 0x14; // inkl. Feststelltaste

    /// <summary>Tasten, die man zum Schreiben/Navigieren braucht – ohne Zusatztaste nicht als Kürzel erlaubt.</summary>
    private static bool IstSchreibtaste(int vk) =>
        vk is 0x08 or 0x09 or 0x0D or 0x20 or >= 0x21 and <= 0x28 or 0x2E
            or >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x6F
            or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2;

    private static string ModifikatorText(Modifikator m)
    {
        var sb = new StringBuilder();
        if (m.HasFlag(Modifikator.Win)) sb.Append("Win+");
        if (m.HasFlag(Modifikator.Strg)) sb.Append("Strg+");
        if (m.HasFlag(Modifikator.Alt)) sb.Append("Alt+");
        if (m.HasFlag(Modifikator.Umschalt)) sb.Append("Umschalt+");
        return sb.ToString();
    }

    private static readonly Dictionary<int, string> Sondertasten = new()
    {
        [0x13] = "Pause", [0x2C] = "Druck", [0x5D] = "Kontextmenü-Taste", [0x91] = "Rollen",
        [0xA6] = "Browser zurück", [0xA7] = "Browser vor", [0xA8] = "Browser aktualisieren",
        [0xA9] = "Browser stopp", [0xAA] = "Browser suchen", [0xAB] = "Browser Favoriten",
        [0xAC] = "Browser Startseite", [0xAD] = "Stumm", [0xAE] = "Leiser", [0xAF] = "Lauter",
        [0xB0] = "Nächster Titel", [0xB1] = "Vorheriger Titel", [0xB2] = "Medien stopp",
        [0xB3] = "Wiedergabe/Pause", [0xB4] = "E-Mail-Taste", [0xB5] = "Medien-Taste",
        [0xB6] = "Start-App-1-Taste", [0xB7] = "Start-App-2-Taste (Rechner)",
    };

    public static string TastenName(int vk, int scan, bool erweitert)
    {
        if (Sondertasten.TryGetValue(vk, out var name)) return name;
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x6F}";
        var sb = new StringBuilder(64);
        var lParam = (scan << 16) | (erweitert ? 1 << 24 : 0);
        if (GetKeyNameText(lParam, sb, sb.Capacity) > 0) return sb.ToString();
        return $"Taste 0x{vk:X2}";
    }

    private static bool Gedrueckt(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static Modifikator AktuelleModifikatoren()
    {
        var m = Modifikator.Keine;
        if (Gedrueckt(VK_LWIN) || Gedrueckt(VK_RWIN)) m |= Modifikator.Win;
        if (Gedrueckt(VK_CONTROL)) m |= Modifikator.Strg;
        if (Gedrueckt(VK_MENU)) m |= Modifikator.Alt;
        if (Gedrueckt(VK_SHIFT)) m |= Modifikator.Umschalt;
        return m;
    }

    private static void UnbelegteTasteSenden()
    {
        var eingaben = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_UNBELEGT } } },
            new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_UNBELEGT, dwFlags = KEYEVENTF_KEYUP } } },
        };
        SendInput((uint)eingaben.Length, eingaben, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameText(int lParam, StringBuilder text, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
