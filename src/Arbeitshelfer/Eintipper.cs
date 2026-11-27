using System.Runtime.InteropServices;
using System.Text;

namespace Arbeitshelfer;

/// <summary>
/// Auto-Type wie bei KeePass: merkt sich das zuletzt aktive fremde Fenster,
/// wechselt dorthin und tippt Tastenanschläge ein.
/// Zeichen werden – wo möglich – als echte Tasten der Tastaturbelegung des Zielfensters
/// gesendet (funktioniert auch in RDP-/VM-Fenstern), sonst als Unicode-Zeichen.
/// </summary>
public sealed class Eintipper : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const ushort VK_CAPITAL = 0x14, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int SW_RESTORE = 9;

    private static readonly HashSet<string> IgnorierteKlassen = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "Progman", "WorkerW",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "ForegroundStaging",
    };

    private readonly WinEventProc _proc;
    private readonly IntPtr _hook;
    private readonly uint _eigenerProzess = (uint)Environment.ProcessId;

    /// <summary>Zuletzt aktives Fenster einer anderen Anwendung.</summary>
    public IntPtr Ziel { get; private set; }

    public Eintipper()
    {
        _proc = VordergrundGewechselt;
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        Merken(GetForegroundWindow());
    }

    private void VordergrundGewechselt(IntPtr hook, uint ereignis, IntPtr hwnd, int obj, int kind, uint thread, uint zeit) =>
        Merken(hwnd);

    private void Merken(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == _eigenerProzess) return;
        if (IgnorierteKlassen.Contains(Klasse(hwnd))) return;
        Ziel = hwnd;
    }

    public string ZielTitel()
    {
        if (Ziel == IntPtr.Zero || !IsWindow(Ziel)) return "–";
        var sb = new StringBuilder(256);
        GetWindowText(Ziel, sb, sb.Capacity);
        var titel = sb.ToString();
        return string.IsNullOrWhiteSpace(titel) ? $"({Klasse(Ziel)})" : titel;
    }

    private static string Klasse(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Wechselt ins Zielfenster und tippt die Teile ein, getrennt durch Tab (bzw. Enter am Ende).</summary>
    public async Task EintippenAsync(string benutzer, string kennwort, bool enterAmEnde)
    {
        var ziel = Ziel;
        if (ziel == IntPtr.Zero || !IsWindow(ziel))
            throw new InvalidOperationException("Kein Zielfenster bekannt – bitte zuerst das Anmeldefenster anklicken.");

        if (IsIconic(ziel)) ShowWindow(ziel, SW_RESTORE);
        for (var versuch = 0; versuch < 3 && GetForegroundWindow() != ziel; versuch++)
        {
            SetForegroundWindow(ziel);
            await Task.Delay(150);
        }
        if (GetForegroundWindow() != ziel)
            throw new InvalidOperationException("Konnte nicht ins Zielfenster wechseln.");

        // Warten, bis keine Umschalt-/Strg-/Alt-/Win-Taste mehr gedrückt ist.
        for (var i = 0; i < 40 && ModifikatorGedrueckt(); i++) await Task.Delay(50);
        await Task.Delay(100);

        var layout = GetKeyboardLayout(GetWindowThreadProcessId(ziel, out _));

        var capsLock = (GetKeyState(VK_CAPITAL) & 1) != 0;
        if (capsLock) await TasteAsync(VK_CAPITAL, layout); // Feststelltaste aus, sonst Groß/klein vertauscht
        try
        {
            if (!string.IsNullOrEmpty(benutzer))
            {
                await TextAsync(benutzer, layout);
                await TasteAsync(VK_TAB, layout);
            }
            await TextAsync(kennwort, layout);
            if (enterAmEnde) await TasteAsync(VK_RETURN, layout);
        }
        finally
        {
            if (capsLock) await TasteAsync(VK_CAPITAL, layout);
        }
    }

    private static bool ModifikatorGedrueckt() =>
        new[] { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN }.Any(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);

    private static async Task TextAsync(string text, IntPtr layout)
    {
        foreach (var c in text)
        {
            var scan = VkKeyScanEx(c, layout);
            if (scan == -1)
            {
                Senden(Unicode(c, false), Unicode(c, true));
            }
            else
            {
                var vk = (ushort)(scan & 0xFF);
                var shift = (scan & 0x100) != 0;
                var strg = (scan & 0x200) != 0;
                var alt = (scan & 0x400) != 0;
                var runter = new List<INPUT>();
                var hoch = new List<INPUT>();
                if (strg) { runter.Add(Taste(VK_CONTROL, layout, false)); hoch.Insert(0, Taste(VK_CONTROL, layout, true)); }
                if (alt) { runter.Add(Taste(VK_MENU, layout, false)); hoch.Insert(0, Taste(VK_MENU, layout, true)); }
                if (shift) { runter.Add(Taste(VK_SHIFT, layout, false)); hoch.Insert(0, Taste(VK_SHIFT, layout, true)); }
                runter.Add(Taste(vk, layout, false));
                hoch.Insert(0, Taste(vk, layout, true));
                Senden(runter.Concat(hoch).ToArray());
            }
            await Task.Delay(10);
        }
    }

    private static async Task TasteAsync(ushort vk, IntPtr layout)
    {
        Senden(Taste(vk, layout, false), Taste(vk, layout, true));
        await Task.Delay(30);
    }

    private static INPUT Taste(ushort vk, IntPtr layout, bool hoch) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = (ushort)MapVirtualKeyEx(vk, 0, layout),
                dwFlags = hoch ? KEYEVENTF_KEYUP : 0,
            },
        },
    };

    private static INPUT Unicode(char c, bool hoch) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | (hoch ? KEYEVENTF_KEYUP : 0) } },
    };

    private static void Senden(params INPUT[] eingaben) =>
        SendInput((uint)eingaben.Length, eingaben, Marshal.SizeOf<INPUT>());

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }

    // ───────────── Win32 ─────────────

    private delegate void WinEventProc(IntPtr hook, uint ereignis, IntPtr hwnd, int obj, int kind, uint thread, uint zeit);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern short VkKeyScanEx(char c, IntPtr layout);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr layout);
    [DllImport("user32.dll")] private static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
}
