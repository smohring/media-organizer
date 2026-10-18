# Konzept: Vorschau-Modus (Plan statt Sofort-Ausführung)

Status: **umgesetzt** · Erstellt: 04.08.2026 · Umgesetzt: 04.08.2026

Ziel: Ein Sortierlauf schreibt nicht mehr sofort auf die Platte, sondern
erzeugt einen **Plan**. Der Nutzer sieht, welche Ordner entstehen würden und
welche Datei wohin käme, kann alles in der Oberfläche umsortieren und stößt
das Schreiben erst bewusst an. Zusätzlich zeigt die App direkt nach Wahl des
Quellordners dessen **vorhandene Struktur**, bevor überhaupt ein Lauf startet.

## 1. Architektur: Plan und Ausführung trennen

Heute schreibt `OrganizePipeline` in der Transfer-Phase direkt auf die Platte;
die Ergebnisansicht zeigt, *was bereits passiert ist*, und jede Korrektur löst
sofort eine Dateioperation aus.

Künftig:

```
Scan → Metadaten → Analyse → Gruppierung → Benennung → [OrganizePlan]
                                                            ↓
                                              Bearbeiten in der UI (kein I/O)
                                                            ↓
                                              "Übernehmen" → PlanExecutor
```

Die Ergebnisansicht wird zum **Plan-Editor**. Drag & Drop, Umbenennen,
Ordner anlegen/löschen arbeiten auf dem Plan — kostenlos, beliebig oft
änderbar, ohne Dateisystem-Konsistenz nachziehen zu müssen. Das **vereinfacht**
den vorhandenen Korrekturcode in `MainViewModel`
(`MoveItemsToFolderAsync`, `RenameFolder`, `DeleteFolder`, …), der heute
jeweils echte Dateioperationen ausführt.

### Datenmodell (neu, in `MediaOrganizer.Core`)

| Typ | Inhalt |
|---|---|
| `OrganizePlan` | Liste `PlannedFolder`, Warnungen, Kennzahlen |
| `PlannedFolder` | Zielpfad, Thema, Datumspräfix, `IsNew` (existiert schon?), `IsCollection`, Herkunftsgruppe |
| `PlannedItem` | `MediaItem`, Zielordner, Operation (Move/Copy), `PlannedStatus`, Begründung |
| `PlannedStatus` | `Move`, `Copy`, `NameConflict` (wird umbenannt), `PossibleDuplicate` |

`PlanExecutor` wendet den Plan an und liefert das bestehende
`OrganizeRunResult` — die Ergebnisansicht bleibt dadurch unverändert nutzbar.

## 2. Zustandsautomat der Hauptfläche

1. **Quellordner gewählt** → sofort **Ist-Zustand**: vorhandene Unterordner mit
   Dateizahl und Thumbnails, rein aus Dateisystem + EXIF (kein LLM).
   Richtwert: ~280 Dateien in 1–2 s.
2. **Lauf läuft** → Fortschritt wie bisher.
3. **Plan fertig** → **Soll-Zustand**, editierbar, mit Umschalter zurück auf
   "Ist" zum Vergleich.
4. **Übernommen** → Ergebnisansicht wie heute.

Kopfzeile mit Zusammenfassung, z.B.:
`12 Ordner (9 neu), 280 Dateien: 274 verschieben, 6 Duplikate übersprungen`

Aktionen: **Übernehmen** und **Verwerfen**. Kennzeichnung je Ordner
(neu / existiert bereits) und je Datei (verschieben / kopieren / Konflikt).

Wird die Ablage-Einstellung (Verschieben/Kopieren, Hierarchie) bei offenem
Plan geändert, rechnet die Vorschau sofort nach — sonst zeigte sie etwas
anderes an, als übernommen würde.

## 3. Knifflige Punkte (bewusst zu entscheiden)

### 3.1 Duplikatprüfung im Plan — entschieden: Variante A
Im Plan läuft nur die billige Prüfung (Datei gleichen Namens im Ziel oder
zweite Plandatei gleichen Namens) → Status `NameConflict`. Sie wird nach jeder
Änderung am Plan neu bestimmt. Der Knopf **"Duplikate jetzt prüfen"** macht auf
Wunsch den SHA-256-Abgleich und stuft bestätigte Treffer auf
`PossibleDuplicate` herunter. Die verbindliche Prüfung bleibt zusätzlich in
`FileOrganizerService.TransferFile` — es wird nie überschrieben, egal was die
Vorschau sagt. Siehe `Planning/PlanConflicts.cs`.

### 3.2 Datumspräfix nach Umsortieren
Der Ordnername leitet sich vom frühesten Foto ab (`MediaGroup.FolderName`).
Nach Umsortieren stimmt das Präfix nicht mehr.
Vorschlag: automatisch neu berechnen, **außer** der Ordner wurde manuell
umbenannt (Flag `HasManualName` am `PlannedFolder`).

### 3.3 Welt ändert sich zwischen Plan und Ausführung
Dateien können zwischenzeitlich verschoben/gelöscht sein. Die Ausführung muss
jede Datei einzeln validieren und Fehler pro Datei melden — die Struktur dafür
existiert bereits (`FileOperationResult` mit `FileOpStatus.Failed`).

### 3.4 Leere Ordner
Ein leerer Ordner wird nie angelegt: `PlanBuilder`/`PlanExecutor` übergehen ihn.
In der Vorschau bleibt er aber sichtbar (mit dem Hinweis "Leer — wird beim
Übernehmen nicht angelegt"), damit ein frisch über "Ordner anlegen …"
erzeugter Ordner nicht sofort wieder verschwindet, bevor Dateien darin liegen.
Über "Leere Ordner entfernen" oder das Papierkorb-Symbol geht er weg.

## 4. Naheliegende Folgefeatures

- **Rückgängig machen** — entschieden: gleich mit umgesetzt.
  Der `PlanExecutor` schreibt jede Operation (Quelle → endgültiges Ziel) samt
  der neu angelegten Verzeichnisse nach `%AppData%\MediaOrganizer\last-run.json`
  (`FileOps/RunJournal.cs`), auch bei Abbruch. "Letzten Lauf rückgängig machen"
  spielt die Liste rückwärts ab — mit Sicherheitsabfrage, die genau benennt,
  was passiert. Datensicherheit gilt auch hier:
  - Verschobene Dateien wandern zurück, aber **nie** über eine Datei, die
    inzwischen wieder am Ursprungsort liegt.
  - Eine **Kopie** wird nur gelöscht, wenn das Original nachweislich (Größe +
    SHA-256) unverändert am Ursprungsort liegt. Sonst bleibt sie stehen und
    das wird gemeldet.
  - Nur beim Lauf **neu angelegte** Verzeichnisse werden entfernt, und nur
    wenn sie leer sind.
  Nachträgliche Korrekturen in der Ergebnisansicht (Verschieben, Umbenennen)
  ziehen das Protokoll mit.
- **"Personen erkennen & neu benennen"** wirkt im Plan-Zustand auf die
  Quelldateien und schreibt nur neue Ordnernamen in den Plan — keine Datei
  wird bewegt. In der Ergebnisansicht arbeitet es wie bisher.
- Optional: Plan über App-Neustarts hinweg speichern (weiterhin offen).

## 5. Umgesetzte Dateien

Neu in `MediaOrganizer.Core`:

- `Models/OrganizePlan.cs` — `OrganizePlan`, `PlannedFolder`, `PlannedItem`,
  `PlannedStatus`
- `Planning/PlanPaths.cs` — Zielpfad-Berechnung an genau einer Stelle
- `Planning/PlanBuilder.cs` — Gruppen → Plan
- `Planning/PlanConflicts.cs` — billige und gründliche Kollisionsprüfung
- `Planning/PlanExecutor.cs` — Plan → `OrganizeRunResult` (+ Protokoll)
- `Planning/SourceSnapshotScanner.cs` — Ist-Zustand (Dateisystem + EXIF)
- `FileOps/RunJournal.cs` — Protokoll und Rückgängigmachen

Geändert:

- `OrganizePipeline.cs` — `RunAsync` → `PlanAsync`; Transfer- und
  EXIF-Schreibphase sind in den `PlanExecutor` gewandert
- `FileOps/FileOrganizerService.cs` — `ExecuteGroupAsync` entfällt;
  `TransferFile` und `BuildTargetDirectory` bleiben
- `ViewModels/MainViewModel.cs` — Zustandsautomat (`Idle`/`Running`/`Plan`/
  `Results`), Ist-Zustand-Scan, Plan-Editor, Übernehmen/Verwerfen/Rückgängig
- `ViewModels/ResultViewModels.cs` — Karten und Kacheln kennen jetzt Plan-,
  Ist- und Ergebnis-Zustand
- `MainWindow.xaml(.cs)` — Ist-Ansicht, Ist/Soll-Umschalter, Kopfzeile mit
  Kennzahlen, Aktionsleiste, Sicherheitsabfrage fürs Rückgängigmachen

## 6. Bewusst beibehalten

Die nachträglichen Korrekturen in der **Ergebnisansicht** (Drag & Drop,
Umbenennen, Ordner anlegen/löschen mit echtem Datei-I/O) bleiben erhalten.
Sie laufen jetzt nur noch dort; im Plan-Zustand arbeiten dieselben Bedienwege
ohne Dateisystemzugriff. Die Methoden in `MainViewModel` verzweigen dafür am
Anfang auf `State == AppState.Plan`.
