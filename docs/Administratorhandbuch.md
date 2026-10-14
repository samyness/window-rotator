# Window Rotator – Administratorhandbuch

Version 1.0 · Zielsystem: Windows 10/11 x64, angemeldete Desktop-Sitzung

## 1. Installation

Das Paket `WindowRotator-<Version>-win-x64.zip` ist **self-contained**: Auf dem Ziel-PC muss keine .NET-Laufzeit installiert sein.

```powershell
# ZIP entpacken, dann im entpackten Ordner (als der Benutzer, unter dem der Monitor laufen soll):
powershell -ExecutionPolicy Bypass -File Install.ps1 -Autostart
```

Wurde das ZIP aus dem Internet/E-Mail geladen, vor dem Entpacken `Unblock-File <ZIP>` ausführen (oder Eigenschaften → „Zulassen“), sonst blockiert Windows SmartScreen die nicht signierte EXE.

`Install.ps1` arbeitet ohne Administratorrechte und

- kopiert `WindowRotator.exe` nach `%LOCALAPPDATA%\Programs\WindowRotator`,
- legt eine Startmenü-Verknüpfung „Window Rotator“ an,
- importiert die Beispielprofile (vorhandene Profile werden nicht überschrieben),
- trägt mit `-Autostart` den Start bei Windows-Anmeldung ein (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, Parameter `--tray`).

Update: neues Paket entpacken und `Install.ps1` erneut ausführen (laufende Instanz wird beendet; Profile bleiben erhalten).
Deinstallation: `Install.ps1 -Uninstall` (Profile und Protokolle bleiben erhalten).

Alternativ kann die EXE ohne Installation aus einem beliebigen Ordner gestartet werden; Autostart lässt sich dann unter **Einstellungen → Programm** einschalten.

### Empfohlene Konfiguration des Produktions-PCs

- Automatische Anmeldung des Anzeige-Benutzers bzw. dauerhaft angemeldete, **nicht gesperrte** Sitzung. Bei gesperrter Sitzung pausiert die Rotation automatisch und läuft nach dem Entsperren weiter.
- Bildschirmschoner und Energiesparmodus für den Monitor deaktivieren.
- Window Rotator, Edge und iVMS-4200 mit **derselben Berechtigungsstufe** starten (alle normal, nicht „Als Administrator“). Windows verhindert, dass ein normaler Prozess erhöht laufende Fenster zuverlässig steuert.
- Edge: Dashboard in einem **eigenen Fenster** öffnen. Bei mehreren Edge-Fenstern im Eintrag einen Titelfilter setzen.
- Edge-Vollbild (F11) erst setzen, nachdem das Fenster auf dem Zielmonitor liegt. Monitorwechsel eines Fensters im laufenden Vollbild ist vor Inbetriebnahme zu testen.
- iVMS-4200: das konkrete Live-View-Fenster in die Rotation übernehmen.

## 2. Programmstart und Betrieb

| Situation | Verhalten |
|---|---|
| Start ohne Parameter | Steuerung wird angezeigt. |
| Start mit `--tray` (Autostart) | Programm startet nur im Infobereich. |
| Zweiter Start | Keine zweite Instanz; die laufende Steuerung wird in den Vordergrund geholt. |
| „Rotation beim Programmstart automatisch starten“ aktiv | Zuletzt verwendetes Profil wird geladen und die Rotation startet. Noch nicht geöffnete Programme werden im Wartezustand alle 5 s erneut gesucht. |
| Fenster geschlossen | Eintrag wird übersprungen (Status „Fenster nicht gefunden“) und in jedem Durchlauf erneut gesucht. |
| Programm neu gestartet (z. B. iVMS-Absturz) | Fenster wird anhand gespeicherter Merkmale neu zugeordnet, alte Fensterhandles werden nie wiederverwendet. |
| Aktivierung fehlgeschlagen | Je nach Profil: bis zu *n* Wiederholungen im Abstand von 2 s, danach überspringen – oder sofort überspringen. Ereignis wird protokolliert. |
| Kein Fenster verfügbar | Wartezustand, Prüfung alle 5 s. |
| Sitzung gesperrt | Rotation pausiert automatisch und wird beim Entsperren fortgesetzt (eine manuelle Pause bleibt bestehen). |
| Windows-Abmeldung/-Neustart | Profil und Einstellungen werden gespeichert. |
| Fenster schließen (X) | Programm läuft im Infobereich weiter (abschaltbar unter Einstellungen). |

### Wie Fenster nach einem Neustart wiedergefunden werden

Gespeichert werden Prozessname, Prozesspfad, Fensterklasse, letzter Fenstertitel und optional ein Titelfilter. Zuordnung in dieser Reihenfolge:

1. Gleicher Prozess (und – falls vorhanden – Titelfilter erfüllt).
2. Fenster mit **exakt** gleichem Titel.
3. Sonst nur, wenn **genau ein** Fenster dieses Prozesses mit gleicher Fensterklasse existiert.
4. Sonst bleibt der Eintrag unzugeordnet (Status „Mehrdeutig“) und wird übersprungen – lieber nichts als das falsche Fenster anzeigen.

Abhilfe bei „Mehrdeutig“: Titelfilter setzen (z. B. „Dashboard“, „Live“) oder das Fenster links markieren und **Markiertes Fenster zuordnen**.

### Fensteraktivierung

Windows garantiert nicht, dass ein Hintergrundprogramm ein fremdes Fenster in den Vordergrund holen darf („Fokussperre“). Der Rotator

1. stellt minimierte Fenster wieder her (Maximierung bleibt erhalten),
2. verschiebt das Fenster auf den Zielmonitor, falls konfiguriert,
3. ruft `SetForegroundWindow` auf; wird das verweigert und ist die Option **ALT-Tastensignal** aktiv, wird zweimal ALT gesendet und erneut aktiviert,
4. prüft nach 1 s, ob das Fenster tatsächlich im Vordergrund ist, und behandelt es andernfalls als Fehlschlag.

Reagiert eine Anwendung sichtbar auf die ALT-Taste (z. B. Menüleiste wird markiert), die Option im Profil deaktivieren.

## 3. Profile und Einstellungen

| Datei | Inhalt |
|---|---|
| `%APPDATA%\WindowRotator\settings.json` | Zuletzt verwendetes Profil, Rotation automatisch starten, Schließen-Verhalten, Protokollstufe |
| `%APPDATA%\WindowRotator\profiles\<Name>.json` | Rotationsliste und Profiloptionen |
| `%LOCALAPPDATA%\WindowRotator\logs\wwr-JJJJMMTT.log` | Tagesprotokolle, 14 Tage Aufbewahrung |

Profile werden bei jeder Änderung automatisch gespeichert (atomar: eine Unterbrechung hinterlässt nie eine halbe Datei). Beschädigte Dateien werden beim Laden als `*.defekt-<Zeitstempel>` gesichert und gemeldet; Profile aus einer neueren Programmversion werden nicht geladen und nicht überschrieben.

Profile können zwischen PCs kopiert werden. Nach dem Kopieren die Einträge prüfen; nicht gefundene Fenster per **Markiertes Fenster zuordnen** neu verknüpfen.

### Profilformat (Schema 1)

```json
{
  "SchemaVersion": 1,
  "Name": "Pilot Edge + iVMS-4200",
  "TargetMonitor": "\\\\.\\DISPLAY2",
  "FailureStrategy": "RetryThenSkip",
  "RetryCount": 2,
  "AltKeyFallback": true,
  "Entries": [
    {
      "Title": "Produktionsdashboard - Microsoft Edge",
      "TitleFilter": "Dashboard",
      "ProcessName": "msedge.exe",
      "ProcessPath": "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe",
      "ClassName": "Chrome_WidgetWin_1",
      "DurationSeconds": 30,
      "Enabled": true
    }
  ]
}
```

| Feld | Bedeutung |
|---|---|
| `TargetMonitor` | Gerätename des Zielmonitors (`null` = Fenster nicht verschieben). Ist der Monitor nicht angeschlossen, bleiben Fenster an ihrem Platz; ein Hinweis wird protokolliert. |
| `FailureStrategy` | `RetryThenSkip` oder `Skip` |
| `RetryCount` | 0–10 Wiederholungen |
| `DurationSeconds` | 3–3600; Werte außerhalb werden beim Laden begrenzt |
| `TitleFilter` | optional, Groß-/Kleinschreibung egal |

Das mitgelieferte Beispielprofil **„Pilot Edge + iVMS-4200“** enthält Annahmen (Dashboard-Titel, iVMS-Prozessname `iVMS-4200.Framework.C.exe`). Auf dem Ziel-PC beide Einträge einmal per **Markiertes Fenster zuordnen** mit den echten Fenstern verknüpfen; die korrekten Merkmale werden danach automatisch gespeichert.

## 4. Protokoll und Diagnose

- **Einstellungen → Protokollstufe:** `Info` für den Betrieb, `Debug` protokolliert zusätzlich jeden Fensterwechsel (für den Dauertest).
- Registerkarte **Protokoll** zeigt die letzten 500 Ereignisse; die Dateien liegen im Protokollordner (Schaltfläche **Ordner öffnen**).
- Unerwartete Fehler werden mit Stacktrace protokolliert; das Programm läuft weiter.

Typische Meldungen:

| Meldung | Ursache / Maßnahme |
|---|---|
| `Fenster nicht gefunden – übersprungen` | Programm geschlossen; wird automatisch wieder zugeordnet. |
| `Mehrdeutig – Titelfilter setzen oder Fenster zuordnen` | siehe Abschnitt 2. |
| `Windows hat den Fensterwechsel verweigert (Fokussperre)` | ALT-Option aktivieren; prüfen, ob die Zielanwendung erhöht läuft. |
| `Fenster wurde nicht in den Vordergrund gebracht` | Ein anderes Fenster (z. B. ein Systemdialog) liegt im Vordergrund. |
| `Anwendung reagiert nicht` | Zielprogramm hängt; es wird übersprungen, bis es wieder reagiert. |
| `Zielmonitor nicht verfügbar` | Monitor getrennt oder umbenannt; Zielmonitor im Profil neu wählen. |

## 5. Abnahme-Checkliste

- [ ] Mindestens drei unterschiedliche Anwendungsfenster lassen sich ohne Konfigurationsdatei per Oberfläche auswählen.
- [ ] Jeder Eintrag hat eine eigene, während der Konfiguration änderbare Anzeigedauer.
- [ ] Im Infobereich: Starten, Pausieren, Fortsetzen und Beenden der Rotation.
- [ ] Zielprogramm schließen bzw. neu starten → kein Absturz, Eintrag wird übersprungen und danach neu zugeordnet.
- [ ] Profil speichern, Windows neu starten, Profil wird geladen (mit Autostart: Rotation startet automatisch).
- [ ] Edge und iVMS-4200 auf dem vorgesehenen Produktions-PC praktisch getestet (inkl. Zielmonitor, Vollbild).
- [ ] 72-Stunden-Dauertest ohne ungeplanten Absturz (Protokollstufe `Debug`, Protokolle danach sichten).

## 6. Bekannte Einschränkungen

- Exklusive Vollbildanwendungen (DirectX-Exklusivmodus) können sich anders verhalten als Edge-F11 oder maximierte Fenster.
- Windows kann die Aktivierung in Einzelfällen verweigern; das wird erkannt, protokolliert und gemäß Profil behandelt.
- Fenster auf anderen virtuellen Desktops und ausgesetzte Store-Apps werden nicht angezeigt.
- Es gibt keine Live-Vorschau der Fenster in der Auswahlliste.
- Die EXE ist nicht signiert; für die Signatur wird ein Code-Signing-Zertifikat benötigt (z. B. `signtool sign /fd SHA256 /a WindowRotator.exe` nach `publish.ps1`).
