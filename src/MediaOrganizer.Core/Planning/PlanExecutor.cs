using MediaOrganizer.Core.FileOps;
using MediaOrganizer.Core.Knowledge;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.Core.Planning;

/// <summary>Führt einen bestätigten Plan aus und liefert das gewohnte
/// <see cref="OrganizeRunResult"/>. Zwischen Planen und Übernehmen kann sich
/// die Welt geändert haben, daher wird jede Datei einzeln geprüft und ein
/// Fehlschlag pro Datei gemeldet statt den Lauf abzubrechen.</summary>
public sealed class PlanExecutor(IImagePreprocessor imagePreprocessor)
{
    public async Task<OrganizeRunResult> ExecuteAsync(
        OrganizePlan plan,
        bool writeDescriptionToExif,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        var journal = new RunJournal { Mode = plan.Mode };
        try
        {
            return await TransferAsync(plan, journal, writeDescriptionToExif, progress, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            // Auch bei Abbruch oder unerwartetem Fehler: Was bereits bewegt
            // wurde, muss zurücknehmbar bleiben.
            journal.Save();
        }
    }

    private async Task<OrganizeRunResult> TransferAsync(
        OrganizePlan plan,
        RunJournal journal,
        bool writeDescriptionToExif,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        var warnings = new List<RunWarning>(plan.Warnings);
        var result = new OrganizeRunResult { Warnings = warnings };
        var total = plan.TotalFiles;
        var done = 0;
        var cancelled = false;

        foreach (var folder in plan.Folders.OrderBy(f => f.GroupStart))
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            if (folder.Items.Count == 0)
            {
                // Leere Ordner werden bewusst nicht angelegt.
                continue;
            }

            var operations = new List<FileOperationResult>(folder.Items.Count);
            try
            {
                journal.CreatedDirectories.AddRange(MissingDirectories(folder.TargetPath));
                Directory.CreateDirectory(folder.TargetPath);
            }
            catch (Exception ex)
            {
                // Ohne Zielordner scheitert der ganze Ordner — jede Datei
                // einzeln als Fehler melden, statt den Lauf abzubrechen.
                RunLog.Warn(RunWarnings.TargetFolder(folder.TargetPath, ex.Message));
                foreach (var planned in folder.Items)
                {
                    operations.Add(new FileOperationResult
                    {
                        Item = planned.Item,
                        Status = FileOpStatus.Failed,
                        Error = $"Zielordner konnte nicht angelegt werden: {ex.Message}",
                    });
                }

                done += operations.Count;
                result.Folders.Add(ToOrganizedFolder(folder, operations));
                continue;
            }

            foreach (var planned in folder.Items)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                progress.Report(new PipelineProgress(
                    PipelinePhase.Transfer, done + operations.Count, total,
                    plan.Mode == TransferMode.Move ? ProgressKeys.Moving : ProgressKeys.Copying,
                    planned.FileName));

                var operation = new FileOperationResult { Item = planned.Item };
                var source = planned.Item.SourcePath;

                if (!File.Exists(source))
                {
                    operation.Status = FileOpStatus.Failed;
                    operation.Error = "Die Datei ist seit dem Planen verschwunden oder wurde verschoben.";
                    operations.Add(operation);
                    continue;
                }

                try
                {
                    var (finalPath, status) = await Task
                        .Run(() => FileOrganizerService.TransferFile(source, folder.TargetPath, plan.Mode), ct)
                        .ConfigureAwait(false);
                    operation.FinalPath = finalPath;
                    operation.Status = status;

                    if (finalPath is not null)
                    {
                        journal.Entries.Add(new JournalEntry
                        {
                            SourcePath = source,
                            FinalPath = finalPath,
                            WasCopy = status == FileOpStatus.Copied,
                        });
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    operation.Status = FileOpStatus.Failed;
                    operation.Error = ex.Message;
                }

                operations.Add(operation);
            }

            done += operations.Count;
            if (operations.Count > 0)
            {
                result.Folders.Add(ToOrganizedFolder(folder, operations));
            }

            if (cancelled)
            {
                break;
            }
        }

        if (cancelled)
        {
            warnings.Add(RunWarnings.Cancelled());
        }
        else if (writeDescriptionToExif)
        {
            WriteDescriptions(result, warnings, ct);
        }

        UpdateNotePaths(result);

        var failed = result.Folders.SelectMany(f => f.Operations).Count(o => o.Status == FileOpStatus.Failed);
        if (failed > 0)
        {
            warnings.Add(RunWarnings.Failed(failed));
        }

        var skipped = result.Folders.SelectMany(f => f.Operations)
            .Count(o => o.Status == FileOpStatus.SkippedDuplicate);
        if (skipped > 0)
        {
            warnings.Add(RunWarnings.Duplicates(skipped));
        }

        RunLog.Log("Plan übernommen: " + string.Join(" | ",
            result.Folders.Select(f => $"{Path.GetFileName(f.FolderPath)} ({f.Operations.Count})")));
        progress.Report(new PipelineProgress(PipelinePhase.Done, total, total, ProgressKeys.Done));
        return result;
    }

    private static OrganizedFolder ToOrganizedFolder(PlannedFolder folder, List<FileOperationResult> operations) =>
        new()
        {
            FolderPath = folder.TargetPath,
            Theme = folder.Theme,
            GroupStart = folder.GroupStart,
            Operations = operations,
        };

    /// <summary>Alle Verzeichnisebenen des Zielpfads, die es noch nicht gibt —
    /// nur diese dürfen beim Rückgängigmachen wieder verschwinden.</summary>
    private static List<string> MissingDirectories(string targetPath)
    {
        var missing = new List<string>();
        for (var dir = targetPath; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir))
            {
                break;
            }

            missing.Add(dir);
        }

        return missing;
    }

    /// <summary>Zieht den zuletzt bekannten Pfad der Bild-Notizen auf den
    /// Zielordner nach.
    /// <para>Ohne diesen Schritt zeigte die Wissensverwaltung nach jedem Lauf
    /// auf die Quelle, aus der die Datei gerade weggezogen ist — die Notiz galt
    /// als verwaist, obwohl sie einwandfrei ist. Die Zuordnung selbst läuft
    /// über den Inhalts-Hash und war nie betroffen; der Pfad dient nur der
    /// Vorschau.</para>
    /// <para>Der Hash wird nur für Aufnahmen berechnet, die tatsächlich eine
    /// Notiz tragen — er kostet je Datei einen Lesezugriff.</para></summary>
    private static void UpdateNotePaths(OrganizeRunResult result)
    {
        var noted = result.Folders
            .SelectMany(f => f.Operations)
            .Where(o => !string.IsNullOrWhiteSpace(o.Item.UserNote) && o.FinalPath is not null)
            .ToList();
        if (noted.Count == 0)
        {
            return;
        }

        var store = KnowledgeStore.Load();
        var updated = 0;
        foreach (var op in noted)
        {
            if (KnowledgeStore.ComputeContentKey(op.FinalPath!) is { } key
                && store.GetNote(key) is not null)
            {
                store.UpdateNotePath(key, op.FinalPath!);
                updated++;
            }
        }

        if (updated > 0)
        {
            store.Save();
            RunLog.Log($"Bild-Notizen: {updated} Pfad(e) auf den Zielordner nachgezogen.");
        }
    }

    private void WriteDescriptions(OrganizeRunResult result, List<RunWarning> warnings, CancellationToken ct)
    {
        var written = 0;
        var skipped = 0;
        foreach (var op in result.Folders.SelectMany(f => f.Operations))
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (op.FinalPath is not { } path
                || op.Item.Kind != MediaKind.Image
                || string.IsNullOrWhiteSpace(op.Item.Description))
            {
                continue;
            }

            if (imagePreprocessor.TryWriteDescription(path, op.Item.Description))
            {
                written++;
            }
            else
            {
                skipped++;
            }
        }

        RunLog.Log($"EXIF-Beschreibungen: {written} geschrieben, {skipped} übersprungen");
        if (written > 0)
        {
            warnings.Add(skipped > 0
                ? RunWarnings.DescriptionsSome(written, skipped)
                : RunWarnings.Descriptions(written));
        }
        else if (skipped > 0)
        {
            // Ohne diesen Hinweis bleibt der Fehlschlag unsichtbar: Der Nutzer
            // schaltet die Option ein, sucht die Beschreibung im Explorer und
            // findet nichts — ohne je zu erfahren, dass nichts geschrieben wurde.
            warnings.Add(RunWarnings.DescriptionsNone(skipped));
        }
    }
}
