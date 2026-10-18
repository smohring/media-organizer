using System.Security.Cryptography;
using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Planning;

/// <summary>Kollisionsprüfung für den Plan — zweistufig:
/// <para>1. Billig (läuft immer, auch nach jedem Umsortieren): Existiert im
/// Ziel bereits eine Datei gleichen Namens, oder kollidieren zwei Plandateien
/// miteinander? → <see cref="PlannedStatus.NameConflict"/>.</para>
/// <para>2. Gründlich (nur auf Knopfdruck): SHA-256-Abgleich, ob es sich um
/// wirklich inhaltsgleiche Dateien handelt → <see cref="PlannedStatus.PossibleDuplicate"/>.</para>
/// Die verbindliche Prüfung bleibt in der Ausführung; hier geht es allein um
/// eine ehrliche Vorschau.</summary>
public static class PlanConflicts
{
    /// <summary>Markiert je Ordner, ob er im Ziel schon existiert.</summary>
    public static void RefreshFolderState(OrganizePlan plan)
    {
        foreach (var folder in plan.Folders)
        {
            folder.IsNew = !Directory.Exists(folder.TargetPath);
        }
    }

    /// <summary>Billige Prüfung: nur Namensgleichheit. Setzt bereits erkannte
    /// Duplikate zurück, weil sich nach dem Umsortieren alles geändert haben
    /// kann.</summary>
    public static void MarkNameConflicts(OrganizePlan plan)
    {
        var fallback = plan.Mode == TransferMode.Copy ? PlannedStatus.Copy : PlannedStatus.Move;

        foreach (var folder in plan.Folders)
        {
            // Bereits im Zielordner liegende Dateinamen.
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(folder.TargetPath))
            {
                try
                {
                    foreach (var existing in Directory.EnumerateFiles(folder.TargetPath))
                    {
                        taken.Add(Path.GetFileName(existing));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Nicht lesbarer Zielordner: Konflikte zeigen sich dann
                    // erst bei der Ausführung — dort wird ebenfalls nie
                    // überschrieben.
                }
            }

            foreach (var item in folder.Items)
            {
                if (taken.Add(item.FileName))
                {
                    item.Status = fallback;
                    item.Reason = null;
                }
                else
                {
                    item.Status = PlannedStatus.NameConflict;
                    item.Reason = $"\"{item.FileName}\" ist im Zielordner bereits belegt — " +
                                  "die Datei wird beim Übernehmen eindeutig umbenannt.";
                }
            }
        }
    }

    /// <summary>Gründliche Prüfung auf Knopfdruck: vergleicht bei jeder
    /// Namenskollision die Inhalte (Größe, dann SHA-256). Liefert die Zahl der
    /// bestätigten Duplikate.</summary>
    public static int MarkContentDuplicates(
        OrganizePlan plan,
        IProgress<PipelineProgress>? progress,
        CancellationToken ct)
    {
        var candidates = plan.Folders
            .SelectMany(f => f.Items.Select(i => (Folder: f, Item: i)))
            .Where(x => x.Item.Status is PlannedStatus.NameConflict or PlannedStatus.PossibleDuplicate)
            .ToList();

        var duplicates = 0;
        var hashes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (folder, item) = candidates[i];
            progress?.Report(new PipelineProgress(
                PipelinePhase.Verify, i, candidates.Count,
                ProgressKeys.CheckingDuplicate, item.FileName, i + 1, candidates.Count));

            // Kandidaten mit gleichem Namen: die Datei im Ziel, die dort schon
            // ausgewichenen Fassungen ("name (2).jpg") und alle vorher
            // einsortierten Plandateien desselben Ordners.
            //
            // Die ausgewichenen Fassungen gehören dazu, weil die Ausführung
            // ebenfalls gegen sie prüft. Ohne sie sagte die Vorschau "wird
            // umbenannt", während die Datei dann übersprungen wird.
            var rivals = new List<string>(ExistingVersions(folder.TargetPath, item.FileName));

            foreach (var other in folder.Items)
            {
                if (ReferenceEquals(other, item))
                {
                    break;
                }

                if (other.FileName.Equals(item.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    rivals.Add(other.Item.SourcePath);
                }
            }

            var identical = rivals.Any(rival => AreIdentical(item.Item.SourcePath, rival, hashes));
            if (identical)
            {
                item.Status = PlannedStatus.PossibleDuplicate;
                item.Reason = $"\"{item.FileName}\" liegt inhaltsgleich bereits im Ziel — " +
                              "die Datei wird übersprungen, die Quelle bleibt unangetastet.";
                duplicates++;
            }
            else
            {
                item.Status = PlannedStatus.NameConflict;
                item.Reason = $"\"{item.FileName}\" ist im Zielordner belegt, hat aber anderen " +
                              "Inhalt — die Datei wird beim Übernehmen eindeutig umbenannt.";
            }
        }

        progress?.Report(new PipelineProgress(
            PipelinePhase.Verify, candidates.Count, candidates.Count,
            ProgressKeys.DuplicateCheckDone));
        return duplicates;
    }

    /// <summary>The file itself in the target and every version that had to
    /// dodge to "name (2).ext" there, in the order the execution walks
    /// them.</summary>
    private static IEnumerable<string> ExistingVersions(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        for (var n = 1; ; n++)
        {
            var candidate = n == 1
                ? Path.Combine(directory, fileName)
                : Path.Combine(directory, $"{stem} ({n}){ext}");

            if (!File.Exists(candidate))
            {
                yield break;
            }

            yield return candidate;
        }
    }

    private static bool AreIdentical(string a, string b, Dictionary<string, byte[]> cache)
    {
        try
        {
            var infoA = new FileInfo(a);
            var infoB = new FileInfo(b);
            if (!infoA.Exists || !infoB.Exists || infoA.Length != infoB.Length)
            {
                return false;
            }

            return Hash(a, cache).AsSpan().SequenceEqual(Hash(b, cache));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nicht lesbar: im Zweifel kein Duplikat — die Ausführung prüft
            // ohnehin erneut und überschreibt nie.
            return false;
        }
    }

    private static byte[] Hash(string path, Dictionary<string, byte[]> cache)
    {
        if (cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        cache[path] = hash;
        return hash;
    }
}
