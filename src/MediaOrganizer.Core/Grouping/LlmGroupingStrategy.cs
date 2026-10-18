using System.Globalization;
using System.Text;
using System.Text.Json;
using MediaOrganizer.Core.Llm;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Services;

namespace MediaOrganizer.Core.Grouping;

/// <summary>Cuts a heap of shots into events by content: the model reads a
/// timeline of image descriptions and says where the boundaries are.
///
/// <para>Applied to the everyday folder and the collecting folder only — the
/// two places where time and GPS have nothing left to say. Everywhere else the
/// EXIF chain decides.</para></summary>
public sealed class LlmGroupingStrategy(
    OllamaClient ollama,
    string model,
    TimeSpan maxEventGap,
    IReadOnlyList<HomeZone>? homeZones = null,
    double gpsRadiusKm = 0,
    Geo.PlaceIndex? places = null,
    Prompts? prompts = null)
{
    private readonly Prompts _prompts = prompts ?? Prompts.German;

    private readonly IReadOnlyList<HomeZone> _homeZones = homeZones ?? [];

    /// <summary>Mehr Items pro Fenster würden Kontext und Antwort-Budget
    /// sprengen; größere Fenster werden zeitlich vor-segmentiert.</summary>
    private const int MaxItemsPerLlmCall = 40;

    /// <summary>Untergrenze für einen Block: Darunter fehlt dem Modell der
    /// Kontext, um überhaupt Ereignisgrenzen zu erkennen.</summary>
    private const int MinChunkItems = 10;

    /// <summary>Radius, in dem für die GPS-Spalte der Zeitleiste gezählt wird,
    /// ob ein Ort mehrfach besucht wurde. Großzügig genug, dass Wohnung,
    /// Supermarkt und Spielplatz desselben Ortes als eine Gegend zählen.</summary>
    private const double RoutinePlaceRadiusKm = 3.0;

    /// <summary>Wie viel näher die andere Gruppe liegen muss, damit eine
    /// Aufnahme räumlich umsortiert wird. Verhindert Wechsel bei Aufnahmen,
    /// die schlicht am Rand eines weitläufigen Ereignisses liegen.</summary>
    private const double MinSnapFactor = 3.0;

    /// <summary>Antwort-Budget der Gruppierung: Das JSON muss alle Indizes
    /// plus Titel enthalten — zu knappe Limits kappen die Antwort, der Parser
    /// scheitert und das ganze Fenster fiele auf EINE Gruppe zurück.</summary>
    private const int GroupNumPredict = 2500;

    /// <summary>Cuts one time window into event groups by content. Shots the
    /// model could not place land in <paramref name="unmatchedSink"/>.</summary>
    internal async Task<List<PartitionedGroup>> PartitionAsync(
        List<MediaItem> window, List<MediaItem> unmatchedSink, CancellationToken ct)
    {
        var result = new List<PartitionedGroup>();
        var chunkIndex = 0;
        foreach (var chunk in ChunkWindow(window, MaxItemsPerLlmCall))
        {
            var groups = await PartitionChunkAsync(chunk, unmatchedSink, ct).ConfigureAwait(false);
            groups = SnapByLocation(groups, gpsRadiusKm, maxEventGap);
            foreach (var group in EnforceMaxGap(groups, maxEventGap))
            {
                result.Add(new PartitionedGroup(group, chunkIndex));
            }

            chunkIndex++;
        }

        return result;
    }

    /// <summary>Räumliche Gegenprobe zur LLM-Entscheidung: Eine Aufnahme, die
    /// weiter als <paramref name="radiusKm"/> vom Schwerpunkt ihrer Gruppe
    /// entfernt liegt, aber innerhalb dieses Radius um den Schwerpunkt einer
    /// anderen Gruppe desselben Aufrufs, wechselt dorthin.
    ///
    /// <para>Bewusst zurückhaltend: Die andere Gruppe muss zusätzlich um den
    /// Faktor <see cref="MinSnapFactor"/> näher liegen und zeitlich in
    /// Reichweite sein — derselbe Ort kann Wochen später erneut besucht worden
    /// sein. Liegt ohnehin alles in derselben Gegend, greift die Regel nie.</para>
    ///
    /// <para>Die Schwerpunkte werden vorab berechnet und während des
    /// Umsortierens nicht nachgeführt: Sonst hinge das Ergebnis davon ab, in
    /// welcher Reihenfolge die Aufnahmen geprüft werden.</para></summary>
    internal static List<MediaGroup> SnapByLocation(
        List<MediaGroup> groups, double radiusKm, TimeSpan maxEventGap)
    {
        if (radiusKm <= 0 || groups.Count < 2)
        {
            return groups;
        }

        var centroids = groups.Select(g => g.Centroid).ToList();
        var moves = new List<(MediaItem Item, MediaGroup From, MediaGroup To)>();

        for (var g = 0; g < groups.Count; g++)
        {
            if (centroids[g] is not { } own)
            {
                continue;
            }

            foreach (var item in groups[g].Items)
            {
                if (item.Location is not { } location)
                {
                    continue;
                }

                var ownDistance = location.DistanceKm(own);
                if (ownDistance <= radiusKm)
                {
                    continue;
                }

                var best = -1;
                var bestDistance = double.MaxValue;
                for (var o = 0; o < groups.Count; o++)
                {
                    if (o == g || centroids[o] is not { } other
                        || TimeDistance(groups[o], item) > maxEventGap)
                    {
                        continue;
                    }

                    var distance = location.DistanceKm(other);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = o;
                    }
                }

                if (best >= 0 && bestDistance <= radiusKm
                    && ownDistance >= bestDistance * MinSnapFactor)
                {
                    moves.Add((item, groups[g], groups[best]));
                    RunLog.Log($"{item.FileName}: {ownDistance:F1} km vom Schwerpunkt von " +
                               $"\"{groups[g].Theme}\", aber {bestDistance:F1} km von " +
                               $"\"{groups[best].Theme}\" — dorthin verschoben.");
                }
            }
        }

        foreach (var (item, from, to) in moves)
        {
            from.Items.Remove(item);
            to.Items.Add(item);
            to.Items.Sort((a, b) => a.TakenLocal.CompareTo(b.TakenLocal));
        }

        return moves.Count == 0 ? groups : [.. groups.Where(g => g.Items.Count > 0)];
    }

    /// <summary>Zeitlicher Abstand einer Aufnahme zum Zeitraum einer Gruppe;
    /// null-Abstand, wenn sie hineinfällt.</summary>
    private static TimeSpan TimeDistance(MediaGroup group, MediaItem item)
    {
        if (item.TakenLocal >= group.StartLocal && item.TakenLocal <= group.EndLocal)
        {
            return TimeSpan.Zero;
        }

        return item.TakenLocal < group.StartLocal
            ? group.StartLocal - item.TakenLocal
            : item.TakenLocal - group.EndLocal;
    }

    // ------------------------------------------------------------------
    // Description phase
    // ------------------------------------------------------------------

    /// <summary>Describes every medium, one model call each.
    ///
    /// <para>Diese Phase ist der Lauf: Bei 10 000 Aufnahmen entfallen rund
    /// 93 % der Gesamtzeit auf sie. Sie läuft deshalb mit mehreren
    /// gleichzeitigen Anfragen an Ollama — dieselben Aufrufe, nur nicht mehr
    /// nacheinander. Gemessen an 80 echten Fotos: 1594 ms je Bild seriell
    /// gegen 1309 ms bei vier gleichzeitig. Kürzere Prompts (3 %) und kleinere
    /// Bilder (9 %, schlechtere Beschreibungen) sind dagegen nachgemessene
    /// Sackgassen.</para></summary>
    internal static async Task DescribeAllAsync(
        IReadOnlyList<MediaItem> items,
        MediaDescriber describer,
        int maxParallel,
        bool reuseBursts,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }

        // Vorladen bleibt außerhalb der Messung: Das Laden eines großen Modells
        // kostet bis zu 40 s und hat mit dem Tempo je Bild nichts zu tun.
        // Zugleich verhindert es, dass die ersten gleichzeitigen Anfragen alle
        // zusammen in den Ladevorgang laufen.
        progress.Report(new PipelineProgress(
            PipelinePhase.Analyze, 0, items.Count, ProgressKeys.LoadingModel));
        using (RunLog.Phase("Model warmup"))
        {
            try
            {
                await describer.WarmupAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Warmup ist reine Optimierung.
            }
        }

        using var phase = RunLog.Phase("Analysis");
        phase.Units = items.Count;

        // Obergrenze: Jede gleichzeitige Anfrage belegt eigenen Kontext im
        // VRAM. Zu viele davon drängen ein knappes Modell auf die CPU und
        // machen es langsamer statt schneller.
        var parallel = Math.Clamp(maxParallel, 1, 16);
        var failures = 0;
        var done = 0;

        // Jede Aufnahme meldet ihren Fortschritt selbst; die Reihenfolge ist
        // bei mehreren gleichzeitigen Analysen ohnehin nicht mehr die der
        // Liste, deshalb zählt allein die Zahl der erledigten.
        async ValueTask AnalyzeAsync(MediaItem item, CancellationToken token)
        {
            try
            {
                await describer.DescribeAsync(item, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Einzelne Analysefehler blockieren den Lauf nicht; das Item
                // wird dann rein zeitlich einsortiert.
                Interlocked.Increment(ref failures);
                RunLog.Log($"Analyse {item.FileName}: FEHLER {ex.Message}");
            }

            var count = Interlocked.Increment(ref done);
            progress.Report(new PipelineProgress(
                PipelinePhase.Analyze, count, items.Count,
                ProgressKeys.Analyzing, item.FileName, count, items.Count));
        }

        Task RunAsync(IReadOnlyList<MediaItem> batch) => Parallel.ForEachAsync(
            batch,
            new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct },
            AnalyzeAsync);

        IReadOnlyList<MediaItem> toAnalyze;
        List<(MediaItem Follower, MediaItem Representative)> followers;
        if (reuseBursts)
        {
            (toAnalyze, followers) = PrepareBursts(items, describer);
        }
        else
        {
            toAnalyze = items;
            followers = [];
        }

        RunLog.Log($"Analyse: {items.Count} Medien, {parallel} gleichzeitig" +
                   (reuseBursts ? $", {toAnalyze.Count} zu analysieren, {followers.Count} aus Serien" : ""));

        await RunAsync(toAnalyze).ConfigureAwait(false);

        // Serien-Übernahme erst NACH der Analyse der Stellvertreter: Vorher
        // gibt es nichts zu übernehmen.
        if (followers.Count > 0)
        {
            var stillOpen = new List<MediaItem>();
            foreach (var (follower, representative) in followers)
            {
                if (string.IsNullOrWhiteSpace(representative.Description)
                    && string.IsNullOrWhiteSpace(representative.Category))
                {
                    // Der Stellvertreter selbst hat nichts geliefert — dann
                    // gibt es nichts zu erben, und die Aufnahme wird ganz
                    // normal einzeln analysiert.
                    stillOpen.Add(follower);
                    continue;
                }

                follower.Description = representative.Description;
                follower.LocationGuess = representative.LocationGuess;
                follower.Category = representative.Category;

                // Bewusst NICHT in den Cache: Die Beschreibung gehört einer
                // anderen Datei. Sonst lieferte ein späterer Lauf mit
                // abgeschalteter Serienerkennung stillschweigend weiter die
                // übernommene Fassung.
                Interlocked.Increment(ref done);
                RunLog.Log($"Analyse {follower.FileName}: aus Serie von {representative.FileName} " +
                           $"übernommen -> \"{RunLog.Truncate(follower.Description ?? "", 80)}\"");
            }

            if (stillOpen.Count > 0)
            {
                RunLog.Log($"Serienerkennung: {stillOpen.Count} Aufnahme(n) ohne brauchbaren " +
                           "Stellvertreter — werden einzeln analysiert.");
                await RunAsync(stillOpen).ConfigureAwait(false);
            }
        }

        if (failures > 0)
        {
            RunLog.Warn(RunWarnings.AnalysisFailed(failures));
        }

        progress.Report(new PipelineProgress(
            PipelinePhase.Analyze, items.Count, items.Count, ProgressKeys.AnalysisDone));
    }

    /// <summary>Teilt die Aufnahmen in "muss analysiert werden" und "erbt von
    /// einer Serien-Nachbarin".
    ///
    /// <para>Der Cache wird zuerst befragt: Wer bereits eine eigene, echte
    /// Beschreibung hat, bekommt keine übernommene. Nur was danach offen
    /// bleibt, wird überhaupt zu Serien zusammengefasst.</para></summary>
    private static (IReadOnlyList<MediaItem> ToAnalyze, List<(MediaItem Follower, MediaItem Representative)> Followers)
        PrepareBursts(IReadOnlyList<MediaItem> items, MediaDescriber describer)
    {
        var open = new List<MediaItem>();
        foreach (var item in items)
        {
            if (!describer.TryApplyCache(item))
            {
                open.Add(item);
            }
        }

        var toAnalyze = new List<MediaItem>();
        var followers = new List<(MediaItem, MediaItem)>();
        foreach (var burst in BurstDetector.Group(open))
        {
            toAnalyze.Add(burst[0]);
            for (var i = 1; i < burst.Count; i++)
            {
                followers.Add((burst[i], burst[0]));
            }

            if (burst.Count > 1)
            {
                RunLog.Log($"Serie erkannt: {burst.Count} Aufnahmen ab {burst[0].FileName} " +
                           $"({burst[0].TakenLocal:HH:mm:ss}–{burst[^1].TakenLocal:HH:mm:ss}) — " +
                           "eine Analyse für alle.");
            }
        }

        return (toAnalyze, followers);
    }

    // ------------------------------------------------------------------
    // Partitionierung
    // ------------------------------------------------------------------

    /// <summary>Teilt ein übergroßes Fenster in Blöcke von höchstens
    /// <see cref="MaxItemsPerLlmCall"/> Aufnahmen.
    ///
    /// <para>Geschnitten wird an der GRÖSSTEN Zeitlücke des zulässigen
    /// Bereichs, nicht an der Tagesgrenze: Die größte Lücke ist meist die
    /// Nachtruhe, und ein Ereignis, das über Mitternacht läuft, hat dort
    /// gerade keine Lücke — es bleibt also zusammen. Ein Schnitt an der
    /// Kalendergrenze hätte es zerrissen.</para>
    ///
    /// <para>Die frühere Fassung schnitt nur, wenn 40 Aufnahmen erreicht
    /// waren UND der Tag wechselte. War die Marke beim Tageswechsel noch
    /// nicht erreicht, wuchs der Block unbegrenzt weiter — in einem echten
    /// Lauf auf 101 Aufnahmen in einem einzigen Aufruf. So viele Indizes
    /// hält das Modell nicht mehr auseinander, und einzelne Aufnahmen
    /// rutschten in die Nachbargruppe.</para></summary>
    internal static IEnumerable<List<MediaItem>> ChunkWindow(List<MediaItem> window, int maxItems)
    {
        if (window.Count <= maxItems)
        {
            yield return window;
            yield break;
        }

        var remaining = window;
        while (remaining.Count > maxItems)
        {
            var cut = FindBestCut(remaining, maxItems);
            yield return remaining[..cut];
            remaining = remaining[cut..];
        }

        if (remaining.Count > 0)
        {
            yield return remaining;
        }
    }

    /// <summary>Schnittstelle mit der größten Zeitlücke. Der Suchbereich hält
    /// beide Seiten auf brauchbarer Größe: Ein Block aus zwei Aufnahmen gäbe
    /// dem Modell keinen Kontext mehr.</summary>
    private static int FindBestCut(List<MediaItem> items, int maxItems)
    {
        var upper = Math.Min(maxItems, items.Count - MinChunkItems);
        var lower = Math.Clamp(upper / 2, MinChunkItems, upper);

        var bestCut = upper;
        var bestGap = TimeSpan.MinValue;
        for (var i = lower; i <= upper; i++)
        {
            var gap = items[i].TakenLocal - items[i - 1].TakenLocal;
            if (gap > bestGap)
            {
                bestGap = gap;
                bestCut = i;
            }
        }

        return bestCut;
    }

    private async Task<List<MediaGroup>> PartitionChunkAsync(
        List<MediaItem> window, List<MediaItem> unmatchedSink, CancellationToken ct)
    {
        if (window.Count == 1)
        {
            return [new MediaGroup { Items = [window[0]] }];
        }

        var timeline = BuildTimeline(window);
        RunLog.Log($"Partition: {window.Count} Items " +
                   $"{window[0].TakenLocal:dd.MM. HH:mm}–{window[^1].TakenLocal:dd.MM. HH:mm}");
        RunLog.Log("Zeitleiste:\n" + RunLog.Truncate(timeline, 4000));

        // Fehlgeschlagene Aufrufe/unbrauchbare Antworten werden einmal
        // wiederholt — und niemals still verschluckt.
        (List<(string Title, List<int> Indices)> Groups, List<int> Unmatched)? parsed = null;
        for (var attempt = 1; attempt <= 2 && parsed is null; attempt++)
        {
            string raw;
            try
            {
                raw = await ollama.ChatAsync(
                        model, _prompts.BuildGroupSystem(true), timeline,
                        numPredict: GroupNumPredict, ct: ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                RunLog.Log($"Partition Versuch {attempt}: AUFRUF FEHLGESCHLAGEN: {ex.Message}");
                continue;
            }

            parsed = ParseAssignment(raw, window.Count);
            RunLog.Log($"Partition Versuch {attempt}: Antwort {(parsed is null ? "UNBRAUCHBAR" : "ok")}: " +
                       RunLog.Truncate(raw, 2000));
        }

        if (parsed is null)
        {
            // LLM nicht nutzbar: gesamtes Fenster als eine Gruppe behalten
            // (lieber zu grob als falsch zerrissen) — aber sichtbar machen.
            RunLog.Log($"Thematische Gruppierung fehlgeschlagen für {window.Count} Aufnahmen " +
                       $"({window[0].TakenLocal:dd.MM.}–{window[^1].TakenLocal:dd.MM.}).");
            RunLog.Warn(RunWarnings.ThematicGroupingFailed(window.Count));
            return [new MediaGroup { Items = window }];
        }

        var (assignment, unmatchedIndices) = parsed.Value;

        // What the model calls "unpassend" simply stays in the pool it came
        // from — the partitioner only ever runs on a pool.
        var excluded = unmatchedIndices.ToHashSet();

        if (assignment.Count == 0)
        {
            if (excluded.Count == window.Count)
            {
                // Alles unpassend → nichts zu gruppieren.
                unmatchedSink.AddRange(window);
                return [];
            }

            return [new MediaGroup { Items = window }];
        }

        var groups = BuildGroups(window, assignment, excluded);

        // Gegenprüfung vor dem Sammelordner.
        foreach (var index in excluded.OrderBy(i => i))
        {
            var item = window[index];

            // Ohne Bildanalyse (z.B. Video ohne verfügbaren Frame) darf ein
            // Medium NIE im Sammelordner landen — das LLM hatte schlicht kein
            // inhaltliches Signal. Es folgt der zeitlich/räumlich nächsten
            // Gruppe.
            if (string.IsNullOrWhiteSpace(item.Description) && string.IsNullOrWhiteSpace(item.Category))
            {
                var fallback = FindNearestGroup(groups, item);
                if (fallback is not null)
                {
                    fallback.Items.Add(item);
                    fallback.Items.Sort((a, b) => a.TakenLocal.CompareTo(b.TakenLocal));
                    RunLog.Log($"{item.FileName}: ohne Analyse — statt Sammelordner der " +
                               $"nächsten Gruppe \"{fallback.Theme}\" zugeordnet.");
                    continue;
                }
            }

            // Auf einer Reise schlägt der Ort die inhaltliche Einschätzung:
            // Was an einem einmalig besuchten Ort entstand, gehört zum
            // Ereignis — die Hüpfburg zwischen zwei Altstadt-Motiven ist Teil
            // des Ausflugs, kein Restposten. Zuhause gilt das ausdrücklich
            // nicht (siehe FindPlaceGroup).
            if (FindPlaceGroup(groups, item, gpsRadiusKm, places, maxEventGap) is { } atPlace)
            {
                atPlace.Items.Add(item);
                atPlace.Items.Sort((a, b) => a.TakenLocal.CompareTo(b.TakenLocal));
                RunLog.Log($"{item.FileName}: als unpassend markiert, entstand aber am Ort von " +
                           $"\"{atPlace.Theme}\" (einmaliger Besuch) — dorthin zugeordnet.");
            }
            else if (FindCategoryMatch(groups, item) is { } match)
            {
                match.Items.Add(item);
                match.Items.Sort((a, b) => a.TakenLocal.CompareTo(b.TakenLocal));
                RunLog.Log($"{item.FileName}: als unpassend markiert, passt aber per Kategorie " +
                           $"zu \"{match.Theme}\" — dorthin zugeordnet.");
            }
            else
            {
                unmatchedSink.Add(item);
            }
        }

        return groups;
    }

    /// <summary>Zeitlich (und, wenn GPS vorhanden, räumlich) nächste Gruppe.
    /// Aufnahmen ohne inhaltliches Signal folgen ihrer Umgebung.</summary>
    private static MediaGroup? FindNearestGroup(List<MediaGroup> groups, MediaItem item)
    {
        MediaGroup? best = null;
        var bestScore = double.MaxValue;

        foreach (var group in groups)
        {
            var hours = Math.Min(
                Math.Abs((group.StartLocal - item.TakenLocal).TotalHours),
                Math.Abs((group.EndLocal - item.TakenLocal).TotalHours));

            // GPS-Nähe zählt stark: 1 km ≈ 1 Stunde Abstand.
            var km = item.Location is { } location && group.Centroid is { } centroid
                ? location.DistanceKm(centroid)
                : 0;

            var score = hours + km;
            if (score < bestScore)
            {
                bestScore = score;
                best = group;
            }
        }

        return best;
    }

    /// <summary>Gruppe am selben Ort, der eine als "unpassend" markierte
    /// Aufnahme zugeschlagen wird, statt sie in den Sammelordner zu geben.
    ///
    /// <para>Greift NUR an Orten, die die Sammlung ein einziges Mal zeigt —
    /// also auf Reisen und Ausflügen. Dort gehört alles Entstandene zum
    /// Ereignis, auch wenn das Motiv aus der Reihe fällt: Eine Hüpfburg
    /// zwischen zwei Altstadt-Motiven ist Teil des Ausflugs, kein Restposten.</para>
    ///
    /// <para>An Alltagsorten greift sie NIE. Dort trägt jede Aufnahme dieselben
    /// Koordinaten, die Ortsprüfung wäre also immer erfüllt und damit wertlos —
    /// Bauplan, Baustelle und Screenshots würden in den Alltagsordner gezogen,
    /// statt im Sammelordner zu landen. Ein Ortsbeleg zählt nur dort, wo der
    /// Ort etwas unterscheidet. Ohne GPS bleibt es bei der Einschätzung des
    /// Modells.</para></summary>
    internal static MediaGroup? FindPlaceGroup(
        List<MediaGroup> groups,
        MediaItem item,
        double radiusKm,
        Geo.PlaceIndex? places,
        TimeSpan maxGap)
    {
        if (radiusKm <= 0 || places is null || item.Location is not { } location
            || places.IsRoutine(location, radiusKm))
        {
            return null;
        }

        MediaGroup? best = null;
        var bestScore = double.MaxValue;
        foreach (var group in groups)
        {
            if (group.Centroid is not { } centroid)
            {
                continue;
            }

            var distance = location.DistanceKm(centroid);
            var apart = TimeDistance(group, item);
            if (distance > radiusKm || apart > maxGap)
            {
                continue;
            }

            // 1 km zählt wie eine Stunde Abstand — dieselbe Gewichtung wie in
            // der übrigen räumlich-zeitlichen Zuordnung.
            var score = apart.TotalHours + distance;
            if (score < bestScore)
            {
                bestScore = score;
                best = group;
            }
        }

        return best;
    }

    /// <summary>Zeitlich nächste Gruppe, in der die Kategorie des Items für
    /// mindestens ein Drittel der Aufnahmen steht — sonst null.</summary>
    private static MediaGroup? FindCategoryMatch(List<MediaGroup> groups, MediaItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Category))
        {
            return null;
        }

        MediaGroup? best = null;
        var bestDistance = long.MaxValue;
        foreach (var group in groups)
        {
            var matching = group.Items.Count(i =>
                string.Equals(i.Category, item.Category, StringComparison.OrdinalIgnoreCase));
            if (matching == 0 || matching * 3 < group.Items.Count)
            {
                continue;
            }

            var distance = Math.Min(
                Math.Abs((group.StartLocal - item.TakenLocal).Ticks),
                Math.Abs((group.EndLocal - item.TakenLocal).Ticks));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = group;
            }
        }

        return best;
    }

    private string BuildTimeline(List<MediaItem> window)
    {
        var sb = new StringBuilder();
        var gpsCache = new Dictionary<(double, double), string>();
        sb.AppendLine("Chronologische Aufnahmen:");
        for (var i = 0; i < window.Count; i++)
        {
            var item = window[i];
            var time = item.TakenLocal.ToString("yyyy-MM-dd ddd HH:mm", CultureInfo.GetCultureInfo("de-DE"));
            var desc = string.IsNullOrWhiteSpace(item.Description) ? "(keine Bildanalyse)" : item.Description;
            var kat = string.IsNullOrWhiteSpace(item.Category) ? "?" : item.Category;
            var gps = DescribeGpsContext(item, gpsCache);
            var note = string.IsNullOrWhiteSpace(item.UserNote) ? "" : $" | Nutzer-Notiz: {item.UserNote}";
            sb.AppendLine($"#{i} | {time} | GPS: {gps} | {desc} | Kategorie: {kat}{note}");
        }

        return sb.ToString();
    }

    /// <summary>GPS-Spalte der Zeitleiste: "Zuhause (Zone)", "unterwegs,
    /// n km von zuhause (bei Ort)" oder nur der aufgelöste Ort.</summary>
    private string DescribeGpsContext(MediaItem item, Dictionary<(double, double), string> cache)
    {
        if (item.Location is not { } location)
        {
            return "?";
        }

        var key = (Math.Round(location.Latitude, 3), Math.Round(location.Longitude, 3));
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        string text;
        var zone = _homeZones.FirstOrDefault(z =>
            z.Covers(location));
        if (zone is not null)
        {
            // Die Art der Zone gehört mit in den Prompt: Ein Urlaubsort ist
            // benannt, aber kein Alltag — sagte die Zeitleiste dort "Zuhause",
            // hielte das Modell die ganze Reise für Alltag.
            text = zone.Kind switch
            {
                ZoneKind.Home => $"Zuhause (\"{zone.Name}\")",
                ZoneKind.Vacation => $"Urlaubsort \"{zone.Name}\" (unterwegs)",
                _ => $"\"{zone.Name}\" (unterwegs)",
            };
        }
        else
        {
            string? place = null;
            try
            {
                place = Geo.OfflineGeocoder.Instance.FindNearest(location)?.DisplayName;
            }
            catch
            {
                // Ortsauflösung ist Komfort.
            }

            var homeZones = _homeZones.Where(z => z.Kind == ZoneKind.Home).ToList();

            // Vor der Zonenrechnung fragen, was die Sammlung selbst über den
            // Ort sagt. Eine handgesetzte Zone kann danebenliegen — lag sie
            // zwei Kilometer neben der Wohnung, meldete die Zeitleiste dort
            // "unterwegs", und das Modell behandelte den Alltag als Ausflug.
            // Die Besuchszahl steht dagegen in den Daten und geht nicht kaputt.
            if (places is not null && places.IsRoutine(location, RoutinePlaceRadiusKm))
            {
                text = $"Alltagsort, mehrfach besucht{(place is null ? "" : $" (bei {place})")}";
            }
            else if (homeZones.Count > 0)
            {
                var distance = homeZones.Min(z => z.Center.DistanceKm(location));
                text = $"unterwegs, {distance:F0} km von zuhause{(place is null ? "" : $" (bei {place})")}";
            }
            else
            {
                text = place ?? $"{location.Latitude:F3}, {location.Longitude:F3}";
            }
        }

        cache[key] = text;
        return text;
    }

    /// <summary>Parst die Gruppenzuordnung: je Gruppe (Titel, Indizes) plus
    /// die als "unpassend" markierten Indizes.</summary>
    private static (List<(string Title, List<int> Indices)> Groups, List<int> Unmatched)? ParseAssignment(
        string raw, int itemCount)
    {
        using var json = LlmJson.TryParse(raw);
        if (json is null || !json.RootElement.TryGetProperty("gruppen", out var gruppen)
            || gruppen.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var result = new List<(string, List<int>)>();
        var seen = new HashSet<int>();

        foreach (var g in gruppen.EnumerateArray())
        {
            var title = LlmJson.GetString(g, "titel") ?? "";
            var indices = new List<int>();
            if (g.TryGetProperty("indizes", out var idxArray) && idxArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var idx in idxArray.EnumerateArray())
                {
                    if (idx.ValueKind == JsonValueKind.Number
                        && idx.TryGetInt32(out var value)
                        && value >= 0 && value < itemCount
                        && seen.Add(value))
                    {
                        indices.Add(value);
                    }
                }
            }

            if (indices.Count > 0)
            {
                result.Add((title, indices));
            }
        }

        var unmatched = new List<int>();
        if (json.RootElement.TryGetProperty("unpassend", out var unpassend)
            && unpassend.ValueKind == JsonValueKind.Array)
        {
            foreach (var idx in unpassend.EnumerateArray())
            {
                if (idx.ValueKind == JsonValueKind.Number
                    && idx.TryGetInt32(out var value)
                    && value >= 0 && value < itemCount
                    && seen.Add(value))
                {
                    unmatched.Add(value);
                }
            }
        }

        return result.Count > 0 || unmatched.Count > 0 ? (result, unmatched) : null;
    }

    /// <summary>Eine Gruppe samt der Nummer des LLM-Aufrufs, aus dem sie
    /// stammt. Die Kombi-Strategie braucht die Unterscheidung: Grenzen
    /// INNERHALB eines Aufrufs sind inhaltliche Entscheidungen und dürfen
    /// nicht wieder zusammengeklebt werden — Grenzen ZWISCHEN zwei Aufrufen
    /// sind bloß technische Schnitte und gehören erneut geprüft.</summary>
    internal readonly record struct PartitionedGroup(MediaGroup Group, int Chunk);

    private static List<MediaGroup> BuildGroups(
        List<MediaItem> window,
        List<(string Title, List<int> Indices)> assignment,
        HashSet<int> excluded)
    {
        var groups = assignment
            .Select(a => new
            {
                a.Title,
                Items = a.Indices.Select(i => window[i]).OrderBy(i => i.TakenLocal).ToList(),
            })
            .Where(g => g.Items.Count > 0)
            .OrderBy(g => g.Items[0].TakenLocal)
            .Select(g =>
            {
                var group = new MediaGroup { Items = g.Items };
                if (!string.IsNullOrWhiteSpace(g.Title))
                {
                    group.Theme = g.Title.Trim();
                }

                return group;
            })
            .ToList();

        // Vom LLM vergessene Indizes der zeitlich nächsten Gruppe zuordnen.
        var assigned = assignment.SelectMany(a => a.Indices).ToHashSet();
        for (var i = 0; i < window.Count; i++)
        {
            if (assigned.Contains(i) || excluded.Contains(i))
            {
                continue;
            }

            var orphan = window[i];
            var nearest = groups
                .OrderBy(g => Math.Min(
                    Math.Abs((g.StartLocal - orphan.TakenLocal).Ticks),
                    Math.Abs((g.EndLocal - orphan.TakenLocal).Ticks)))
                .First();
            nearest.Items.Add(orphan);
            nearest.Items.Sort((a, b) => a.TakenLocal.CompareTo(b.TakenLocal));
        }

        return groups;
    }

    /// <summary>Harte Zeitregel: Gruppen mit interner Lücke über
    /// <paramref name="maxGap"/> werden aufgetrennt (Titel bleibt erhalten,
    /// Eindeutigkeit stellt die Benennung später sicher).</summary>
    internal static List<MediaGroup> EnforceMaxGap(List<MediaGroup> groups, TimeSpan maxGap)
    {
        var result = new List<MediaGroup>();
        foreach (var group in groups)
        {
            var parts = TimeClustering.SplitAtGaps(
                group.Items.OrderBy(i => i.TakenLocal).ToList(), maxGap);
            foreach (var part in parts)
            {
                result.Add(new MediaGroup { Items = part, Theme = group.Theme });
            }
        }

        return result.OrderBy(g => g.StartLocal).ToList();
    }
}
