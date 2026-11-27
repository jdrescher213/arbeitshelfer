# Arbeitshelfer

Kleine Windows-App für die tägliche Arbeit: ein **schwebender Info-Knopf**, der
immer im Vordergrund bleibt. Ein Klick (oder das Tastenkürzel, Standard **Win+Q**) klappt die
Leiste aus; die Symbole stehen links:

| Symbol | Funktion | Kürzel |
|---|---|---|
| ☑ ToDo | ToDo-Liste mit Zeiterfassung (Play/Pause je Eintrag) | Strg+1 |
| 💬 KI | Öffnet die KI-Webseite (Adresse in den Einstellungen) – Browser und Profil einstellbar (z. B. immer ein bestimmtes Firefox-Profil) | Strg+2 |
| 🔑 LAPS | Windows-LAPS-Kennwort abfragen, anzeigen und kopieren | Strg+3 |
| ⚙ Einstellungen | Größe (Schieberegler, wirkt sofort), Tastenkürzel, Autostart, LAPS-Benutzer, KI-Adresse | Strg+0 |

**Esc** klappt zu.

## Bedienung

- **Klick** auf den Knopf: auf-/zuklappen. **Ziehen**: verschieben (Position wird gemerkt).
- **i-Symbol im Infobereich** (unten rechts neben der Uhr, ggf. unter dem Pfeil `^`):
  Linksklick holt den Knopf nach vorn. Rechtsklick: *Knopf anzeigen*, *Einstellungen*,
  *Position zurücksetzen*, *Einfache Darstellung*, *Beenden*.
- Liegt der Knopf am rechten oder unteren Bildschirmrand, öffnet die Leiste nach links bzw. oben.
- **Knopf unsichtbar?** (z. B. per Remote-Desktop/Citrix) → Infobereich-Symbol → Rechtsklick →
  *Einfache Darstellung (bei Remote-Desktop)*. Die App startet dann ohne Transparenz neu.

## ToDo & Zeiterfassung

- Ticketnummer und/oder Betreff eingeben, **Enter** legt den Eintrag an
  (**Umschalt+Enter**: anlegen und Zeit sofort starten).
- **▶** startet die Zeit, **⏸** pausiert. Es läuft immer nur eine Uhr; Starten einer anderen pausiert die laufende.
  Während eine Zeit läuft, hat der Knopf einen grünen Punkt.
- Die **Summe** steht hinter dem Eintrag; ein Klick darauf zeigt die **Einzelbuchungen**.
- Abgehakte Einträge verschwinden aus der Liste (Uhr wird dabei angehalten);
  **Erledigte einblenden** zeigt sie wieder. Es wird nichts gelöscht.
- Beim Beenden/Abmelden wird die laufende Zeit beendet. Nach einem Absturz endet sie
  zum Zeitpunkt der letzten Speicherung (läuft minütlich).

## LAPS

- **Eintippen (wie KeePass Auto-Type):** Klick auf einen der letzten Rechner (oder ⌨ beim angezeigten
  Kennwort, oder **Umschalt+Enter** im Eingabefeld) → die App wechselt ins **zuletzt aktive Fenster**
  und tippt `.\administrator` TAB Kennwort ENTER. Das Zielfenster steht unter der Liste („Eintippen in: …“).
  Benutzer und Enter sind in den Einstellungen änderbar. Zeichen werden als Tasten der Tastaturbelegung
  des Zielfensters gesendet (funktioniert auch in RDP-Fenstern). Nicht möglich: UAC-Abfragen und Fenster,
  die als Administrator laufen (Windows blockiert das).
- Rechnername eingeben, **Enter** → Kennwort wird **angezeigt** (Consolas, gut lesbar) und
  in die Zwischenablage kopiert. Anzeige blendet sich nach 60 s aus, Zwischenablage wird nach 60 s
  geleert (nur falls das Kennwort noch drinsteht). Nicht im Zwischenablageverlauf (Win+V).
- Die **letzten 3 Rechnernamen** stehen darunter; Klick = eintippen, 👁 = nur anzeigen/kopieren.
- Es werden **nur Rechnernamen** gespeichert, nie Kennwörter.
- Abfrage über `Get-LapsADPassword` (PowerShell-Modul **LAPS**) mit den Rechten des angemeldeten Benutzers.

## Eigene Tastatureingaben

Unter ⚙ Einstellungen → *Eigene Tastatureingaben* beliebig viele anlegen: Name, Symbol
(eingebaut oder aus einer `.exe`/`.ico`/Bilddatei), optional Benutzer, Kennwort, *Enter am Ende*.
Sie erscheinen als Symbole im LAPS-Bereich unter den letzten Abfragen; ein Klick tippt
`[Benutzer TAB] Kennwort [ENTER]` ins zuletzt aktive Fenster (z. B. eine Eingabe „nur Kennwort“ für eine
Anwendung und eine mit Enter für die RDP-Anmeldung).

## Verschlüsselung

Einstellungen (inkl. Kennwörter der eigenen Eingaben) und ToDos/Zeiten werden mit **Windows-DPAPI**
für das angemeldete Benutzerkonto verschlüsselt – kein Startpasswort nötig. Nur derselbe Windows-Benutzer
kann sie lesen; eine kopierte Datei ist für andere wertlos. Alte unverschlüsselte Dateien werden beim
nächsten Speichern automatisch verschlüsselt. Ist eine Datei nicht lesbar (z. B. neues Benutzerprofil),
wird sie als `*.unlesbar-<Datum>` aufgehoben statt überschrieben.

## Einstellungen

Alle über ⚙ in der Leiste (die Datei `%APPDATA%\Arbeitshelfer\einstellungen.json` ist verschlüsselt):

| Schlüssel | Bedeutung | Standard |
|---|---|---|
| `Groesse` | Vergrößerung (1.0 = 100 %) | `2.0` |
| `Tastenkuerzel` | `Win+Q`, `Strg+Alt+I`, `Strg+Alt+A`, `Strg+Umschalt+Leertaste`, `Rollen-Taste`, `Pause-Taste`, `Aus` | `Win+Q` |
| `LapsBenutzer` | Benutzer beim Eintippen (leer = nur Kennwort) | `.\administrator` |
| `EintippenMitEnter` | Enter nach dem Kennwort | `true` |
| `KiAdresse` | Adresse der KI (beim ersten Klick auf KI abgefragt) | leer |
| `ZwischenablageLeerenNachSekunden` | 0 = nie leeren | `60` |
| `KennwortAnzeigenSekunden` | Anzeigedauer des Kennworts | `60` |
| `EinfacheDarstellung` | ohne Transparenz zeichnen | `false` |

ToDos und Zeitbuchungen: `%APPDATA%\Arbeitshelfer\todos.json`. Fehlerprotokoll: `fehler.log` im selben Ordner.

## Updates

Der Arbeitshelfer prüft beim Start und dann stündlich, ob es auf GitHub ein neueres Release gibt,
und installiert es automatisch, sobald die Leiste eingeklappt ist und keine Zeit läuft
(kurzer Neustart, Sprechblase „aktualisiert“). Abschaltbar unter ⚙ Einstellungen; dort und im
Menü des Infobereich-Symbols gibt es auch *Nach Updates suchen*. Voraussetzung: die `.exe` liegt
in einem Ordner mit Schreibrechten (z. B. `C:\Tools`), nicht unter `C:\Program Files`.

Ein Release entsteht automatisch bei jedem Push auf `main`, wenn `<Version>` in
`src/Arbeitshelfer/Arbeitshelfer.csproj` erhöht wurde.

## Bauen

Jeder Push auf `main` baut über GitHub Actions eine einzelne `Arbeitshelfer.exe`
(Reiter **Actions** → letzter Lauf → *Artifacts*, bzw. unter **Releases**).
Es muss keine .NET-Laufzeit installiert sein.

Lokal (mit .NET-10-SDK): `dotnet run --project src/Arbeitshelfer`
