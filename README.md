# CS2 Manager

Ein Windows-Desktoptool (WPF / C# / .NET 10) für die Konfiguration und Vorbereitung von Counter-Strike 2.

**Entwickler:** SaschaDe  
**Leitspruch:** Optimize. Configure. Play.

## Funktionen

- CS2-Prozess erkennen und den Ablauf rund um den Spielstart koordinieren
- CPU-Affinität und Prozesspriorität für CS2 setzen
- Windows-TCP-Empfangsfenster-Auto-Tuning optional temporär ändern und den vorherigen Wert beim Aufräumen wiederherstellen
- Integrierter Standby-Listen-Cleaner; ISLC kann je nach Einstellung optional verwendet werden
- Optional weitere Programme vorübergehend schließen und nach CS2 wieder starten
- Optionaler verzögerter Steam-Connect-Link
- Einstellungen und Wiederherstellungszustand lokal speichern

Der konkrete Funktionsumfang richtet sich nach dem aktuellen Quellcode und den Einstellungen in der Anwendung.

## Voraussetzungen

- Windows 10/11, 64 Bit
- .NET 10 SDK
- Visual Studio 2022 oder neuer mit Workload **.NET-Desktopentwicklung**
- Steam und Counter-Strike 2 für den praktischen Einsatz

## Projekt erstellen

1. `CS2Manager.sln` in Visual Studio öffnen.
2. Konfiguration **Release** wählen.
3. **Erstellen → Projektmappe neu erstellen**.

Alternativ in einem Terminal im Verzeichnis der Projektmappe:

```powershell
dotnet build .\CS2Manager.sln -c Release
```

## Portable Veröffentlichung

In Visual Studio das Projekt `CS2Manager` veröffentlichen und das Profil `Win-x64-SelfContained` (falls im Projekt vorhanden) verwenden.

Alternativ:

```powershell
dotnet publish .\CS2Manager\CS2Manager.csproj -c Release -r win-x64 --self-contained true
```

Die veröffentlichte Anwendung liegt im Publish-Ausgabeverzeichnis von .NET. Für GitHub sollte die ausführbare Veröffentlichung als **Release-Asset** hochgeladen werden; nicht in den normalen Quellcode-Upload aufnehmen.

## Hinweise und Verantwortung

- Einige Funktionen ändern Windows-Systemeinstellungen oder beenden Prozesse. Prüfe jede Einstellung und verwende das Programm nur, wenn du die Auswirkungen verstehst.
- Administratorrechte können für bestimmte Systemfunktionen erforderlich sein.
- Änderungen an Affinität, Priorität, TCP-Einstellungen oder Speicherbereinigung garantieren keine höheren FPS oder besseren Frametimes. Ergebnisse hängen vom jeweiligen System ab.
- Sichere wichtige Daten und notiere ursprüngliche Systemeinstellungen, bevor du System-Tuning-Funktionen verwendest.
- Kompatibilität mit FACEIT Anti-Cheat oder anderen Anti-Cheat-Systemen wird nicht garantiert. Verwende keine Funktion, die gegen die Regeln eines Spiels oder Dienstes verstößt.
- Dieses Projekt steht in keiner Verbindung zu Valve, Steam, Counter-Strike oder FACEIT und wird von diesen nicht unterstützt.

## Spenden

Die Nutzung ist kostenlos. Falls du die Arbeit freiwillig unterstützen möchtest, kannst du später einen freiwilligen Spendenlink (z. B. GitHub Sponsors oder PayPal) hier ergänzen. Eine Spende ist keine Voraussetzung für die Nutzung.

## Lizenz

Für dieses Repository wurde bislang keine Lizenz ausgewählt. Ohne eine hinzugefügte Lizenz gelten die üblichen urheberrechtlichen Beschränkungen; andere dürfen den Quellcode nicht automatisch weiterverwenden oder weiterverbreiten.
