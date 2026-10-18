using System.Collections.Concurrent;
using MediaOrganizer.Core.Faces;
using MediaOrganizer.Core.Grouping;
using MediaOrganizer.Core.Knowledge;
using MediaOrganizer.Core.Llm;
using MediaOrganizer.Core.Metadata;
using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Naming;
using MediaOrganizer.Core.Planning;
using MediaOrganizer.Core.Services;
using MediaOrganizer.Core.Video;

namespace MediaOrganizer.Core;

/// <summary>Orchestriert einen kompletten Lauf:
/// Scan → Metadaten → (Analyse) → Gruppierung → Benennung → Plan.
/// <para>Die Pipeline schreibt bewusst nichts auf die Platte: Sie liefert
/// einen <see cref="OrganizePlan"/>, den der Nutzer prüfen und umsortieren
/// kann. Erst der <see cref="PlanExecutor"/> führt ihn aus.</para></summary>
public sealed class OrganizePipeline(
    AppSettings settings,
    IImagePreprocessor imagePreprocessor)
{
    /// <summary>How much wider than the event gap the model's merge check
    /// looks. At the same width it could never reach the pairs the pause split
    /// created — those are more than one gap apart by definition.</summary>
    private const int LlmMergeWindowFactor = 3;

    public async Task<OrganizePlan> PlanAsync(
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        RunLog.Start();
        // One model for everything; old configurations naming an unsupported
        // tag are raised to the smallest supported one.
        var model = VisionModels.EnsureSupported(settings.ModelTag);

        // Bestimmt, in welcher Sprache das Modell antwortet — und damit, wie
        // Ordner heißen. Gehört deshalb ins Protokoll: Ein Lauf mit unerwartet
        // englischen Ordnernamen ist sonst nicht von einem Fehler zu
        // unterscheiden.
        var prompts = Prompts.For(settings.OutputLanguage);

        RunLog.Log($"Modus={settings.GroupingMode}, Modell={model}, " +
                   $"Ausgabesprache={settings.OutputLanguage}, " +
                   $"Sammelordner={settings.CollectUnmatched} ('{settings.UnmatchedFolderName}'), " +
                   $"Mindestgröße={settings.MinItemsPerFolder}, " +
                   $"Dubletten überspringen={settings.SkipDuplicates}, " +
                   $"GPS-Radius={settings.GpsGroupRadiusKm} km, " +
                   $"Region-Radius={settings.RegionMergeRadiusKm} km, " +
                   $"Heimatzonen konfiguriert={settings.HomeZones.Count}, " +
                   $"gleichzeitige Analysen={settings.MaxParallelAnalyses}, " +
                   $"Serien-Wiederverwendung={settings.ReuseBurstDescriptions}, " +
                   // Together, because the split only means anything with a
                   // hierarchy — a run whose collecting folder did not split
                   // is otherwise indistinguishable from a broken one.
                   $"hierarchy={settings.TargetHierarchy}, " +
                   $"split dateless folders={settings.SplitDatelessByHierarchy}");

        var warnings = new List<RunWarning>();
        var ffmpeg = new FfmpegService(settings.FfmpegPath);
        var useLlm = settings.GroupingMode is GroupingMode.Combined;

        OllamaClient? ollama = null;
        DescriptionCache? cache = null;
        try
        {
            if (useLlm)
            {
                ollama = new OllamaClient(settings.OllamaEndpoint);
                if (!await ollama.IsReachableAsync(ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        $"Ollama ist unter {settings.OllamaEndpoint} nicht erreichbar. " +
                        "Bitte Ollama starten oder den Gruppierungsmodus \"Rein EXIF\" wählen.");
                }

                if (!ffmpeg.IsAvailable)
                {
                    warnings.Add(RunWarnings.Ffmpeg());
                }

                if (settings.UseAnalysisCache)
                {
                    cache = DescriptionCache.Load(DescriptionCache.DefaultPath);
                }
            }

            // --- Scan ---
            progress.Report(new PipelineProgress(PipelinePhase.Scan, 0, 1, ProgressKeys.Scanning));
            List<(string Path, MediaKind Kind)> files;
            using (var scanPhase = RunLog.Phase("Scan"))
            {
                files = ScanSourceFolder(warnings);
                scanPhase.Units = files.Count;
            }

            if (files.Count == 0)
            {
                return EmptyPlan([.. warnings, RunWarnings.NoMedia()]);
            }

            // --- Metadaten ---
            var items = await ReadMetadataAsync(files, progress, ct).ConfigureAwait(false);

            // Dubletten vor allem anderen: Was ein zweites Mal dasselbe Bild
            // ist, soll weder beschrieben noch gruppiert noch einsortiert
            // werden. Die schlechtere Fassung bleibt unangetastet liegen.
            if (settings.SkipDuplicates)
            {
                items = DropDuplicates(items, progress, ct);
            }

            // Nutzer-Notizen (per Inhalts-Hash) an die Items heften.
            var knowledge = KnowledgeStore.Load();
            var notePathsChanged = false;
            using (var notePhase = RunLog.Phase("Notes"))
            {
                // Ohne hinterlegte Notiz gibt es nichts zuzuordnen — dann muss
                // auch keine einzige Datei dafür gelesen werden. Der Hash
                // kostet je Datei einen Lesezugriff, und der Normalfall ist
                // eine Handvoll Notizen auf Tausende Aufnahmen.
                List<MediaItem> notesToCheck = knowledge.HasNotes ? items : [];
                notePhase.Units = notesToCheck.Count;

                var notesDone = 0;
                foreach (var item in notesToCheck)
                {
                    ct.ThrowIfCancellationRequested();
                    if (++notesDone % 250 == 0 || notesDone == notesToCheck.Count)
                    {
                        progress.Report(new PipelineProgress(
                            PipelinePhase.Metadata, notesDone, notesToCheck.Count,
                            ProgressKeys.ReadingNotes, notesDone, notesToCheck.Count));
                    }

                    if (KnowledgeStore.ComputeContentKey(item.SourcePath) is not { } key)
                    {
                        continue;
                    }

                    item.UserNote = knowledge.GetNote(key);
                    if (item.UserNote is null)
                    {
                        continue;
                    }

                    // Pfad nachziehen, damit die Wissensverwaltung eine
                    // Vorschau zeigen kann.
                    knowledge.UpdateNotePath(key, item.SourcePath);
                    notePathsChanged = true;

                    // Eigene Miniatur nachholen, solange die Datei greifbar
                    // ist. Danach steht die Notiz vollständig in den
                    // Nutzerdaten und übersteht es, wenn der Quellordner
                    // geleert, umbenannt oder gelöscht wird.
                    if (!knowledge.HasThumbnail(key) && CreateThumbnail(item) is { } jpeg)
                    {
                        knowledge.TryStoreThumbnail(key, jpeg);
                    }
                }
            }

            // Ohne dieses Speichern ginge der wiedergefundene Pfad verloren
            // und die Verwaltung zeigte weiter "nicht auffindbar".
            if (notePathsChanged)
            {
                knowledge.Save();
            }

            // Neue Notizen in dauerhafte Wissensbasis-Fakten destillieren,
            // bevor Analyse/Benennung sie nutzen.
            if (useLlm && knowledge.UndistilledNotes.Count > 0)
            {
                progress.Report(new PipelineProgress(
                    PipelinePhase.Analyze, 0, 1, ProgressKeys.DistillingKnowledge));
                await new KnowledgeDistiller(ollama!, model, prompts)
                    .DistillAsync(knowledge, ct).ConfigureAwait(false);
            }

            var noExif = items.Count(i => i.TimestampSource == TimestampSource.FileSystem);
            if (noExif > 0)
            {
                warnings.Add(RunWarnings.NoCaptureTime(noExif));
            }

            var fromFileName = items.Count(i => i.TimestampSource == TimestampSource.FileName);
            if (fromFileName > 0)
            {
                warnings.Add(RunWarnings.TimeFromFileName(fromFileName));
            }

            var withGps = items.Count(i => i.Location is not null);
            RunLog.Log($"{items.Count} Dateien, davon {withGps} mit GPS, {noExif} ohne Aufnahme-Zeitstempel");
            if (withGps * 2 < items.Count)
            {
                warnings.Add(RunWarnings.FewLocations(items.Count - withGps, items.Count));
            }

            // Same provider for both: the face models and the scene encoder
            // live in one folder and are fetched the same way.
            var faceModels = new FaceModelProvider();

            // --- Gesichtserkennung (optional) ---
            if (settings.FaceRecognitionEnabled)
            {
                var personStore = PersonStore.Load();
                if (!faceModels.IsInstalled)
                {
                    warnings.Add(RunWarnings.FaceModels());
                }
                else if (personStore.Persons.Count == 0)
                {
                    warnings.Add(RunWarnings.NoPersons());
                }
                else
                {
                    RecognizePeople(
                        items, personStore, faceModels, settings.OutputLanguage, progress, ct);
                }
            }

            // --- Heimatzonen (konfiguriert oder automatisch vermutet) ---
            IReadOnlyList<HomeZone> effectiveZones = settings.HomeZones;
            if (effectiveZones.Count == 0
                && Geo.HomeZoneDetector.DetectImplicitHome(items, prompts.DefaultHomeZoneName)
                    is { } implicitZone)
            {
                effectiveZones = [implicitZone];
                var placeHint = Geo.PlaceResolver
                    .ResolveVisitedPlaces([.. items.Where(i =>
                        i.Location is { } l && implicitZone.Center.DistanceKm(l) <= 1.5)], 1)
                    .FirstOrDefault()?.DisplayName ?? $"{implicitZone.Latitude:F3}, {implicitZone.Longitude:F3}";
                warnings.Add(RunWarnings.GuessedHomeZone(placeHint));
            }

            RunLog.Log(effectiveZones.Count == 0
                ? "Keine Heimatzone (weder konfiguriert noch erkennbar) — Zuhause-Logik inaktiv."
                // The kind belongs in the log: only "Home" makes a zone
                // everyday life, and that is the default for every zone.
                : $"Wirksame Zonen: {string.Join("; ", effectiveZones.Select(z => $"{z.Name} [{z.Kind}] ({z.Latitude:F4}, {z.Longitude:F4}, r={z.RadiusKm:0.#} km)"))}");

            // A home zone bigger than the distance that makes a group travel
            // switches the whole trip logic off inside it — and "home" is the
            // default kind, so a zone added in passing does that silently. One
            // measured case: a zone named "Urlaub", kind home, radius 100 km,
            // which turned 912 holiday shots into everyday life.
            foreach (var zone in effectiveZones.Where(z =>
                         z.Kind == ZoneKind.Home
                         && z.RadiusKm >= GroupContextClassifier.TripDistanceKm))
            {
                warnings.Add(RunWarnings.HomeZoneTooWide(zone.Name, zone.RadiusKm));
            }

            // --- Gruppierung ---
            // Ein Modell für Analyse, Gruppierung und Benennung. Die
            // Wissensbasis-Fakten gehen in BEIDES: in den Vision-Prompt (damit
            // das Modell bekannte Personen beim Namen nennt statt zu
            // umschreiben) und in die Benennung. Gegen erfundene Personen
            // steht im Beschreibungs-Prompt eine strikte Anti-Halluzinations-
            // Regel — der Grund, warum Modelle unter 12B nicht zur Wahl stehen.
            var describer = useLlm
                ? new MediaDescriber(ollama!, model, imagePreprocessor, ffmpeg, cache, knowledge.Facts, prompts)
                : null;
            // Zeit ist bewusst nur grobe Sicherheitsgrenze (2 Tage) — die
            // eigentliche Trennung kommt aus dem Inhalt.
            // DIE eine Zeitgrenze der Anwendung. Sie beantwortet überall
            // dieselbe Frage — "gehört das noch zusammen?" — und wird deshalb
            // auch überall aus derselben Einstellung genommen: beim Trennen
            // (SplitByPause), beim Zählen der Besuche eines Ortes (PlaceIndex,
            // daran hängt Alltag gegen Reise) und beim Zusammenführen
            // (MergeSameRegion).
            //
            // Vorsicht bei großen Werten: Je größer die Zahl, desto mehr
            // Besuche verschmelzen zu einem — ein wöchentlich fotografiertes
            // Zuhause käme bei sieben Tagen auf einen einzigen Besuch und sähe
            // damit aus wie eine Reise. Deshalb ist der Standard klein.
            var maxEventGap = TimeSpan.FromDays(Math.Clamp(settings.MaxEventGapDays, 0.1, 60));

            // Welche Orte zeigt die Sammlung mehrfach? Daran hängt, ob der Ort
            // ein Beleg für Zusammengehörigkeit ist (Ausflug) oder nicht
            // (Alltagsort wie zuhause).
            var places = new Geo.PlaceIndex(items, maxEventGap);

            // Neither mode cuts anything here: one group over the whole
            // collection. Place and time cut it afterwards, each exactly once.
            // With the model this step also describes every image.
            IGroupingStrategy strategy = useLlm
                ? new CombinedGroupingStrategy(
                    describer!, settings.MaxParallelAnalyses, settings.ReuseBurstDescriptions)
                : new ExifGroupingStrategy(TimeSpan.MaxValue, splitOnPlaceChange: false);

            var groups = await strategy.GroupAsync(items, progress, ct).ConfigureAwait(false);

            // The configured name of the collecting folder — needed this early
            // because the shots without GPS go straight into it.
            var unmatchedName = GroupNamingService.Sanitize(
                string.IsNullOrWhiteSpace(settings.UnmatchedFolderName)
                    ? prompts.DefaultUnmatchedFolderName
                    : settings.UnmatchedFolderName);

            // --- The EXIF chain, identical in both modes ---

            // Only a shot's own coordinates say anything about where it was.
            // Everything without GPS leaves before any place name falls on it.
            groups = SeparateUnlocatedShots(groups, settings.CollectUnmatched, unmatchedName);

            // The everyday shots FIRST, before any cut by place: otherwise the
            // 2 km radius bundles flat, playground and supermarket into one
            // place whose centre lands back inside the zone.
            groups = MergeEverydayGroups(groups, effectiveZones, warnings);

            // Then by place — a time window is never split otherwise, however
            // far the camera travelled.
            groups = SplitByPlace(groups, settings.GpsGroupRadiusKm);

            // And last the pause, which separates two visits to the same place.
            groups = SplitByPause(groups, maxEventGap);

            // At an everyday place the calendar day is the boundary as well:
            // the event gap is 24 hours and would keep the next morning in.
            groups = SplitEverydayPlacesByDay(
                groups, settings.RegionMergeRadiusKm, places, effectiveZones);

            groups = MergeSameRegion(
                groups, settings.RegionMergeRadiusKm, maxEventGap, places, effectiveZones);

            // The model's counter-check to the clock: two halves of one trip
            // that the pause tore apart find each other again. Its window is
            // deliberately wider than the pause — at the same width it could
            // never reach the pairs the pause created.
            if (useLlm)
            {
                groups = await new LlmGroupMerger(ollama!, model, effectiveZones, prompts)
                    .MergeAsync(groups, maxEventGap * LlmMergeWindowFactor, progress, ct)
                    .ConfigureAwait(false);
            }

            groups = MergeUndersizedGroups(
                groups, settings.MinItemsPerFolder, keepLocated: !useLlm);

            foreach (var group in groups.Where(g => g.IsCollection))
            {
                group.Theme = unmatchedName;
            }

            // The everyday folder is complete now, so events may be cut out of
            // it — by how the shots LOOK, not by asking the model. Only out of
            // THAT folder; see the method.
            if (useLlm && faceModels.IsSceneEncoderInstalled)
            {
                using var embedder = new SceneEmbedder(faceModels);
                var sceneCache = SceneCache.Load();
                try
                {
                    var vectors = await EmbedScenesAsync(
                            groups, embedder, sceneCache, imagePreprocessor, ffmpeg, progress, ct)
                        .ConfigureAwait(false);
                    groups = SplitEverydayIntoEvents(
                        groups, vectors, settings.MinItemsPerFolder, maxEventGap, progress, ct);

                    // After the events, so a rescued shot can also land in one
                    // of them — and before naming, so it has a say in the name.
                    groups = RescueFromCollection(groups, vectors, maxEventGap, progress);
                }
                finally
                {
                    sceneCache.Save();
                }
            }
            else if (useLlm)
            {
                // No encoder, no evidence — everything stays in the everyday
                // folder rather than being grouped on a guess.
                RunLog.Log("Scene encoder missing — the everyday folder was not cut into events.");
            }

            // --- Benennung ---
            var naming = new GroupNamingService(
                useLlm ? ollama : null, model, imagePreprocessor, ffmpeg,
                effectiveZones, knowledge.Facts, settings.MaxParallelAnalyses, prompts);
            using (var namePhase = RunLog.Phase("Naming"))
            {
                namePhase.Units = groups.Count;
                await naming.NameGroupsAsync(groups, progress, ct).ConfigureAwait(false);
            }

            // Both modes: a group neither the model nor the place database could
            // name has no evidence behind it. With the model this catches the
            // events cut out of the collecting folder that turned out to be
            // none — they go back where they came from.
            if (settings.CollectUnmatched)
            {
                groups = MoveUnnamedToCollection(groups, unmatchedName, warnings);
            }

            // Last, after naming: the parts of a split folder share one name,
            // and asking for it once per period would cost time and hand back
            // different answers for what is one folder.
            if (settings.SplitDatelessByHierarchy)
            {
                groups = SplitDatelessByPeriod(groups, settings.TargetHierarchy, warnings);
            }

            // Zuletzt die Themenordner IM Sammelordner — nach allem, was ihn
            // noch füllt, und nach seiner Zerlegung in Zeiträume. Der Name des
            // Sammelordners steht da bereits, und die Themenordner tragen ihn
            // als Elternteil.
            //
            // Nach der Zerlegung, nicht davor: Die Mindestgröße gilt sonst über
            // die ganze Sammlung, und was sie EINMAL überschritten hat, wurde
            // anschließend auf die Monate verteilt — gemessen 167 Themenordner,
            // daraus 563 Ordner, 290 davon mit einer einzigen Datei.
            if (useLlm && settings.CollectUnmatched)
            {
                groups = SplitCollectionByCategory(
                    groups, settings.MinItemsPerFolder, prompts.FileKindCategories);
            }

            // --- Plan aufstellen (kein Datei-I/O) ---
            var totalFiles = groups.Sum(g => g.Items.Count);
            progress.Report(new PipelineProgress(
                PipelinePhase.Plan, 0, 1, ProgressKeys.BuildingPlan));

            // Während des Laufs gesammelte Diagnose-Warnungen durchreichen.
            warnings.AddRange(RunLog.Warnings.Distinct());

            var plan = PlanBuilder.Build(groups, settings, warnings);
            RunLog.Log("Plan aufgestellt: " + string.Join(" | ",
                plan.Folders.Select(f => $"{Path.GetFileName(f.TargetPath)} ({f.Items.Count}" +
                                         $"{(f.IsNew ? ", neu" : "")})")));

            progress.Report(new PipelineProgress(
                PipelinePhase.Done, totalFiles, totalFiles, ProgressKeys.PlanReady));
            return plan;
        }
        finally
        {
            cache?.Save();
            ollama?.Dispose();

            // Erst ganz am Ende: Die Übersicht ist der Block, nach dem man
            // sucht, wenn ein Lauf zu lange gedauert hat — sie gehört ans Ende
            // der Datei, auch wenn der Lauf abgebrochen wurde.
            RunLog.LogPhaseSummary();
            RunLog.Flush();
        }
    }

    /// <summary>EXIF-only mode: puts the groups nothing could name into the
    /// collecting folder, provided the user has that folder switched on.
    ///
    /// <para>Without GPS there is no place, and without the model there is no
    /// topic — the folder would be "20250709_Aufnahmen", which is the date the
    /// file name already carries plus a word that says nothing. In one measured
    /// collection that was 282 of 419 folders for 2944 of 16 176 files:
    /// messenger images and screenshots, almost all of it.</para>
    ///
    /// <para>This deliberately stretches what the collecting folder is for —
    /// elsewhere it takes in-between shots, not everything that failed to be
    /// placed. It stays tied to the switch: turn the collecting folder off and
    /// the dated folders come back.</para></summary>
    internal static List<MediaGroup> MoveUnnamedToCollection(
        List<MediaGroup> groups,
        string unmatchedName,
        List<RunWarning> warnings)
    {
        var unnamed = groups.Where(g => g.IsUnnamed && !g.IsCollection).ToList();
        if (unnamed.Count == 0)
        {
            return groups;
        }

        var remaining = groups.Except(unnamed).ToList();
        var collection = remaining.FirstOrDefault(g => g.IsCollection);
        if (collection is null)
        {
            collection = new MediaGroup { Items = [], IsCollection = true, Theme = unmatchedName };
            remaining.Add(collection);
        }

        var moved = 0;
        foreach (var group in unnamed)
        {
            collection.Items.AddRange(group.Items);
            moved += group.Items.Count;
        }

        collection.Items.Sort((x, y) => x.TakenLocal.CompareTo(y.TakenLocal));
        RunLog.Log($"{unnamed.Count} unnamed group(s) with {moved} shot(s) moved into " +
                   $"\"{collection.Theme}\" — no place could be resolved for them.");
        warnings.Add(RunWarnings.Unnamed(unnamed.Count, moved, collection.Theme));
        return remaining;
    }

    /// <summary>Splits the dateless folders — the collecting folder and the
    /// everyday folder — along the configured hierarchy: one per year, or one
    /// per month, instead of one for the whole collection.
    ///
    /// <para>The parts keep the name and stay without a date prefix; what
    /// changes is where they sit, from beside the year levels to inside them
    /// (<c>2026\Vermischt</c>). Which level comes from the target hierarchy, so
    /// there is no second place to configure it — and no level at all under
    /// <see cref="TargetHierarchy.Flat"/>, where nothing happens and the run
    /// says so rather than leaving the switch looking ignored.</para>
    ///
    /// <para>Runs after naming and after every merging step: everything that
    /// feeds the collecting folder has to have fed it before it is cut
    /// up.</para></summary>
    internal static List<MediaGroup> SplitDatelessByPeriod(
        List<MediaGroup> groups,
        TargetHierarchy hierarchy,
        List<RunWarning> warnings)
    {
        if (!groups.Any(g => g.IsDateless && g.Items.Count > 0))
        {
            return groups;
        }

        if (hierarchy == TargetHierarchy.Flat)
        {
            RunLog.Log("Splitting the dateless folders is on, but the target hierarchy is flat — " +
                       "there is no level to split along, nothing was split.");
            warnings.Add(RunWarnings.SplitNeedsHierarchy());
            return groups;
        }

        var byMonth = hierarchy == TargetHierarchy.YearMonth;
        var result = new List<MediaGroup>();

        foreach (var group in groups)
        {
            if (!group.IsDateless || group.Items.Count == 0)
            {
                result.Add(group);
                continue;
            }

            var parts = group.Items
                .GroupBy(i => byMonth
                    ? new DateTime(i.TakenLocal.Year, i.TakenLocal.Month, 1)
                    : new DateTime(i.TakenLocal.Year, 1, 1))
                .OrderBy(p => p.Key)
                .Select(p => new MediaGroup
                {
                    Items = [.. p.OrderBy(i => i.TakenLocal)],
                    Theme = group.Theme,
                    IsCollection = group.IsCollection,
                    IsEveryday = group.IsEveryday,
                    IsUnnamed = group.IsUnnamed,
                    // The parent travels with the part. Dropping it is what
                    // made a themed subfolder dateless no longer, so it climbed
                    // out of the collecting folder and grew a date in its name.
                    InsideFolder = group.InsideFolder,
                    IsPeriodScoped = true,
                })
                .ToList();

            result.AddRange(parts);
            RunLog.Log($"Dateless folder \"{group.Theme}\" with {group.Items.Count} shot(s) split " +
                       $"into {parts.Count} part(s): " + string.Join(", ", parts.Select(p =>
                           $"{p.StartLocal.ToString(byMonth ? "yyyy-MM" : "yyyy")} ({p.Items.Count})")));
        }

        return result;
    }

    /// <summary>Takes the lesser copies out of the run: the same picture twice,
    /// proven by a pixel comparison. Only the best copy is sorted; the others
    /// stay where they are and are not touched.</summary>
    private static List<MediaItem> DropDuplicates(
        List<MediaItem> items,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        List<List<MediaItem>> groups;
        using (var phase = RunLog.Phase("Duplicates"))
        {
            phase.Units = items.Count;
            groups = DuplicateFinder.Find(items, DuplicateCache.Load(), progress, ct);
        }

        if (groups.Count == 0)
        {
            return items;
        }

        var dropped = groups.SelectMany(g => g.Skip(1)).ToHashSet();
        foreach (var group in groups)
        {
            RunLog.Log($"Duplicate: keeping \"{Path.GetFileName(group[0].SourcePath)}\", " +
                       $"leaving {string.Join(", ", group.Skip(1)
                           .Select(i => $"\"{Path.GetFileName(i.SourcePath)}\""))}");
        }

        RunLog.Log($"{dropped.Count} shot(s) in {groups.Count} group(s) are the same picture twice.");
        return [.. items.Where(i => !dropped.Contains(i))];
    }

    /// <summary>Computes one vector per shot of the given groups and keeps them
    /// in the cache. Serial by design — one OpenCV net, and at 26 ms a shot the
    /// whole collection costs about three minutes once.</summary>
    private static async Task<Dictionary<MediaItem, float[]>> EmbedScenesAsync(
        IEnumerable<MediaGroup> groups,
        SceneEmbedder embedder,
        SceneCache cache,
        IImagePreprocessor preprocessor,
        FfmpegService ffmpeg,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        var vectors = new Dictionary<MediaItem, float[]>();
        var items = groups.SelectMany(g => g.Items).Distinct().ToList();
        var fresh = 0;
        var done = 0;

        using var phase = RunLog.Phase("Scene vectors");
        phase.Units = items.Count;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            // Reported as "stuck": without this the phase runs for minutes
            // behind an unchanged progress line. Every 25th, so the line moves
            // without the report itself costing time.
            if (++done % 25 == 0 || done == items.Count)
            {
                progress.Report(new PipelineProgress(
                    PipelinePhase.Group, done, items.Count,
                    ProgressKeys.SceneVectors, done, items.Count));
            }

            var key = SceneCache.ComputeKey(item.SourcePath);
            if (key is null)
            {
                continue;
            }

            if (cache.TryGet(key) is { } cached)
            {
                vectors[item] = cached;
                continue;
            }

            float[]? vector;
            try
            {
                // Videos through ffmpeg first, the Windows preview only as a
                // fallback — the same order the describer uses, because the
                // preview is absent on trimmed Windows installations.
                //
                // And SEVERAL frames, averaged: the first frame of a video is
                // often dark, blurred or a transition, and such a picture is
                // similar to everything. Reported case — a video of Anna with
                // her toys landed in "Spülmaschine ausbauen".
                if (item.Kind == MediaKind.Video)
                {
                    var frames = await ffmpeg
                        .ExtractFramesAsync(item.SourcePath, item.Duration, maxFrames: 3, ct: ct)
                        .ConfigureAwait(false);
                    if (frames.Count == 0
                        && preprocessor.GetVideoFrameJpeg(item.SourcePath) is { } shell)
                    {
                        frames = [shell];
                    }

                    vector = SceneEmbedder.Average([.. frames.Select(embedder.Embed)]);
                }
                else
                {
                    vector = preprocessor.GetJpegForAnalysis(item.SourcePath) is { } jpeg
                        ? embedder.Embed(jpeg)
                        : null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or NotSupportedException)
            {
                continue;
            }

            if (vector is null)
            {
                continue;
            }

            cache.Store(key, vector);
            vectors[item] = vector;
            fresh++;
        }

        RunLog.Log($"Scene vectors: {vectors.Count} of {items.Count} shot(s), {fresh} freshly computed.");
        return vectors;
    }

    /// <summary>Takes shots back out of the collecting folder and into an
    /// existing group when they LOOK like it and fall into its time window.
    ///
    /// <para>The one thing that can still be shown about a shot without GPS: a
    /// WhatsApp picture of the party looks like the party's own pictures and was
    /// taken on the same day. It attaches to something proven instead of
    /// founding something new — which is why the threshold is stricter than the
    /// one inside a pool: a wrong attachment spoils a folder that was
    /// right.</para></summary>
    internal static List<MediaGroup> RescueFromCollection(
        List<MediaGroup> groups,
        IReadOnlyDictionary<MediaItem, float[]> vectors,
        TimeSpan maxGap,
        IProgress<PipelineProgress> progress)
    {
        var collections = groups.Where(g => g.IsCollection && g.Items.Count > 0).ToList();
        var targets = groups.Where(g => !g.IsDateless && g.Items.Count > 0).ToList();
        if (collections.Count == 0 || targets.Count == 0)
        {
            return groups;
        }

        // Compared against the group as it was BEFORE the rescue, never against
        // one that is growing. Otherwise a rescued shot becomes a member and the
        // next candidate is measured against it — the group drifts away from
        // what it was. Measured: of 46 candidates 4 fit the original group, the
        // folder ended up with 31 shots on a day that has 11.
        var before = targets.ToDictionary(g => g, g => (IReadOnlyList<MediaItem>)[.. g.Items]);

        var moved = 0;
        var done = 0;
        using var phase = RunLog.Phase("Rescue");
        phase.Units = collections.Sum(c => c.Items.Count);

        foreach (var collection in collections)
        {
            var taken = new List<MediaItem>();
            foreach (var item in collection.Items)
            {
                if (++done % 25 == 0 || done == phase.Units)
                {
                    progress.Report(new PipelineProgress(
                        PipelinePhase.Group, done, phase.Units,
                        ProgressKeys.Rescuing, done, phase.Units));
                }

                if (!vectors.TryGetValue(item, out var vector)
                    || SceneClustering.FindHome(item, vector, targets, before, vectors, maxGap)
                        is not { } home)
                {
                    continue;
                }

                home.Items.Add(item);
                taken.Add(item);
            }

            if (taken.Count == 0)
            {
                continue;
            }

            collection.Items.RemoveAll(taken.Contains);
            moved += taken.Count;
        }

        if (moved == 0)
        {
            return groups;
        }

        foreach (var group in targets)
        {
            group.Items.Sort((x, y) => x.TakenLocal.CompareTo(y.TakenLocal));
        }

        RunLog.Log($"{moved} shot(s) without GPS moved from the collecting folder into an " +
                   "existing group — same look, same time window.");
        return [.. groups.Where(g => g.Items.Count > 0)];
    }

    /// <summary>Cuts events out of the everyday folder by how the shots LOOK.
    /// Parts reaching <paramref name="minItems"/> become dated event groups;
    /// everything else stays in it.
    ///
    /// <para><b>The collecting folder is deliberately left alone.</b> Its shots
    /// carry no GPS and, being messenger images and screenshots, a timestamp
    /// that says when they arrived rather than when they were taken. A block of
    /// them holds unrelated occasions, the model is asked for boundaries and
    /// always finds some, and the time guard cannot correct it because the
    /// timestamps do lie close together. Reported repeatedly: "Anna im Wald"
    /// holding a family gathering and an afternoon at home, "Ben mit Freunden
    /// beim Trinken" holding Japan and a bar. A dated folder claims that its
    /// contents belong together on that day — for these shots neither half can
    /// be shown.</para></summary>
    internal static List<MediaGroup> SplitEverydayIntoEvents(
        List<MediaGroup> groups,
        IReadOnlyDictionary<MediaItem, float[]> vectors,
        int minItems,
        TimeSpan maxEventGap,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        var pools = groups.Where(g => g.IsEveryday && g.Items.Count > minItems).ToList();
        if (minItems <= 0 || pools.Count == 0)
        {
            return groups;
        }

        var result = new List<MediaGroup>(groups);
        var events = 0;
        var moved = 0;

        using var phase = RunLog.Phase("Grouping: events");
        phase.Units = pools.Sum(p => p.Items.Count);

        var done = 0;
        foreach (var pool in pools)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(new PipelineProgress(
                PipelinePhase.Group, ++done, pools.Count,
                ProgressKeys.GroupingByTopic, done, pools.Count));

            // First cut at the pauses, then group each stretch by how it looks.
            // The pause is the boundary an appearance encoder cannot see: every
            // shot is the same child in the same flat.
            var carved = TimeClustering
                .SplitAtGaps([.. pool.Items.OrderBy(i => i.TakenLocal)], SceneClustering.OccasionPause)
                // Inside a stretch time has had its say — a party shot without a
                // break must not be chopped into equal lengths. Only the
                // pictures decide there.
                .SelectMany(stretch => SceneClustering.Cluster(
                    stretch, vectors, TimeSpan.MaxValue))
                .Where(c => c.Count >= minItems)
                .Select(c => new MediaGroup { Items = [.. c.OrderBy(i => i.TakenLocal)] })
                .ToList();
            if (carved.Count == 0)
            {
                continue;
            }

            var taken = carved.SelectMany(g => g.Items).ToHashSet();
            pool.Items.RemoveAll(taken.Contains);
            result.AddRange(carved);
            events += carved.Count;
            moved += taken.Count;

            RunLog.Log($"{carved.Count} event(s) cut out of \"{pool.Theme}\" for {taken.Count} shot(s), " +
                       $"sizes: {string.Join(", ", carved.Select(g => g.Items.Count))}");
        }

        if (events == 0)
        {
            return groups;
        }

        progress.Report(new PipelineProgress(
            PipelinePhase.Group, 1, 1, ProgressKeys.GroupsFormed, result.Count));
        RunLog.Log($"{events} event folder(s) with {moved} shot(s) came out of the everyday folder.");
        return [.. result.Where(g => g.Items.Count > 0)];
    }

    /// <summary>Cuts themed subfolders out of the collecting folder:
    /// "Vermischt\Screenshot", "Vermischt\Dokumente".
    ///
    /// <para>The collecting folder is the last resort and it grows large — in
    /// one measured collection 5024 of 6039 files. What the model recognised as
    /// one KIND while describing does not become an event, but it does not have
    /// to lie loose in a heap of thousands either: screenshots belong with
    /// screenshots.</para>
    ///
    /// <para>Uses the category the describer already wrote on every item
    /// (<c>MediaItem.Category</c>, one word: "Screenshot", "Gerät",
    /// "Baustelle") — no second model call, and nothing to keep in step. Only
    /// kinds with at least <paramref name="minItems"/> shots get a folder;
    /// below that the heap is the better place. Without the model there are no
    /// categories, so in EXIF-only mode this does nothing at all.</para>
    ///
    /// <para>Runs LAST, after <see cref="SplitDatelessByPeriod"/>, and treats
    /// every part of the collecting folder on its own. That order is what makes
    /// the minimum size mean anything: it is measured inside the folder the
    /// subfolder will actually sit in. Counted across the whole collection
    /// instead, eight shots of one kind spread over eight months passed the
    /// minimum once and then became eight folders holding one file each.</para></summary>
    /// <param name="fileKinds">Only these categories may become a folder. The
    /// describer's word names a subject just as often as a kind, and a folder
    /// "Fahrrad" holding five unrelated bicycles helps nobody.</param>
    internal static List<MediaGroup> SplitCollectionByCategory(
        List<MediaGroup> groups,
        int minItems,
        IReadOnlySet<string> fileKinds)
    {
        var collections = groups.Where(g => g.IsCollection && g.Items.Count > 0).ToList();
        if (collections.Count == 0)
        {
            return groups;
        }

        var result = new List<MediaGroup>(groups);
        var folders = 0;
        var movedTotal = 0;
        var parentName = collections[0].Theme;

        foreach (var collection in collections)
        {
            var byCategory = collection.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Category)
                            && fileKinds.Contains(i.Category!.Trim()))
                .GroupBy(i => i.Category!.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= Math.Max(2, minItems))
                .ToList();

            if (byCategory.Count == 0)
            {
                continue;
            }

            var moved = new HashSet<MediaItem>();
            foreach (var kind in byCategory)
            {
                var items = kind.OrderBy(i => i.TakenLocal).ToList();
                result.Add(new MediaGroup
                {
                    Items = items,
                    Theme = GroupNamingService.Sanitize(kind.Key),
                    InsideFolder = collection.Theme,
                    // The subfolder inherits the level of its parent part —
                    // without it the parent path collapses to the base folder
                    // and the subfolder leaves the year level with it.
                    IsPeriodScoped = collection.IsPeriodScoped,
                });

                foreach (var item in items)
                {
                    moved.Add(item);
                }
            }

            collection.Items.RemoveAll(moved.Contains);
            folders += byCategory.Count;
            movedTotal += moved.Count;

            // A part whose whole content was one kind is empty now. It would
            // plan no folder anyway; dropping it keeps the group list honest.
            if (collection.Items.Count == 0)
            {
                result.Remove(collection);
            }

            RunLog.Log($"{byCategory.Count} themed subfolder(s) in \"{collection.Theme}\"" +
                       $"{(collection.IsPeriodScoped ? $" ({collection.StartLocal:yyyy-MM})" : "")} " +
                       $"for {moved.Count} shot(s): " +
                       string.Join(", ", byCategory.Select(k => $"{k.Key} ({k.Count()})")));
        }

        if (folders == 0)
        {
            return groups;
        }

        RunLog.Log($"{folders} themed subfolder(s) in \"{parentName}\" for {movedTotal} shot(s).");
        return result;
    }

    /// <summary>EXIF-only mode: cuts every group into places.
    ///
    /// <para>Without the model a group is a stretch of TIME — inside eight hours
    /// the grouping never splits at all, however far the camera travelled. The
    /// name then comes from the dominant place, and everything else in the
    /// window carries it. Reported case: a folder "20260614_Beispielburg" holding
    /// shots taken at home, 200 m from the home zone; the drive home and the
    /// evening after it sat in the same eight hours as the outing.</para>
    ///
    /// <para>Clustered greedily along the timeline: a shot joins the running
    /// cluster while it stays within <paramref name="radiusKm"/> of its centre,
    /// otherwise it opens a new one. Greedy and not "nearest of all clusters" on
    /// purpose — a walk through a town drifts, and every step should stay with
    /// the one before it rather than jump back to a cluster left an hour
    /// ago.</para>
    ///
    /// <para>The pieces of one outing find each other again in
    /// <c>MergeSameRegion</c>, which joins neighbouring groups within the region
    /// radius — but only where the place is not an everyday one. That is the
    /// asymmetry that makes this work: the shots at home stay separate and are
    /// gathered by <see cref="MergeEverydayGroups"/>.</para></summary>
    internal static List<MediaGroup> SplitByPlace(
        List<MediaGroup> groups,
        double radiusKm)
    {
        if (radiusKm <= 0)
        {
            return groups;
        }

        var result = new List<MediaGroup>();
        var extra = 0;

        foreach (var group in groups)
        {
            // Datumslose Ordner sind Sammelbecken über die ganze Sammlung —
            // Sammelordner und Alltagsordner. Sie nach Orten zu zerlegen zerriss
            // den Alltagsordner in Häufchen, seit er VOR dieser Stelle gebildet
            // wird.
            if (group.IsDateless || group.Items.Count < 2)
            {
                result.Add(group);
                continue;
            }

            var clusters = new List<List<MediaItem>>();
            var centre = default(GeoPoint?);
            List<MediaItem>? current = null;

            foreach (var item in group.Items.OrderBy(i => i.TakenLocal))
            {
                if (item.Location is not { } location)
                {
                    // Ohne Koordinaten bleibt die Aufnahme, wo sie ist; um sie
                    // kümmert sich SeparateUnlocatedShots.
                    current?.Add(item);
                    continue;
                }

                if (current is null || centre is not { } c || c.DistanceKm(location) > radiusKm)
                {
                    current = [];
                    clusters.Add(current);
                    centre = location;
                }

                current.Add(item);

                // Schwerpunkt mitziehen: Sonst reißt ein Spaziergang beim
                // Verlassen des Radius um den ERSTEN Punkt ab, statt dem Weg zu
                // folgen.
                var located = current.Where(i => i.Location is not null).Select(i => i.Location!.Value).ToList();
                centre = new GeoPoint(located.Average(p => p.Latitude), located.Average(p => p.Longitude));
            }

            // Wer denselben Ort verlässt und zurückkommt, hat beim Aufbau zwei
            // Häufchen erzeugt — sie gehören zusammen. Ohne diesen Schritt
            // entstanden "Musterstadt", "Musterstadt 2" und "Musterstadt 3"
            // desselben Tages.
            clusters = MergeSamePlaceClusters(clusters, radiusKm);

            if (clusters.Count <= 1)
            {
                result.Add(group);
                continue;
            }

            foreach (var cluster in clusters)
            {
                result.Add(new MediaGroup
                {
                    Items = cluster,
                    Theme = group.Theme,
                    IsEveryday = group.IsEveryday,
                    IsUnnamed = group.IsUnnamed,
                    IsPeriodScoped = group.IsPeriodScoped,
                });
            }

            extra += clusters.Count - 1;
        }

        if (extra > 0)
        {
            RunLog.Log($"{extra} additional group(s) from splitting by place " +
                       $"(radius {radiusKm:0.#} km): a place folder holds only shots taken there.");
        }

        return result;
    }

    /// <summary>EXIF-only mode: cuts a group wherever it pauses for longer than
    /// the configured event gap.
    ///
    /// <para>Runs AFTER the split by place, and that order is the whole point.
    /// The grouping measures the pause between consecutive shots of the entire
    /// collection, and at a place photographed every few days that chain never
    /// breaks: 28.04 → 30.04 → 03.05 → … is one group across weeks, dated by its
    /// first shot. Reported case: a folder "20260428_Beispielstadt an der Alb"
    /// opening with shots from a completely different date. Applied inside one
    /// place, the same number does what it promises — it separates two visits
    /// while quiet days within one trip stay together.</para></summary>
    /// <summary>At an everyday place a group must not span calendar days.
    ///
    /// <para>The event gap is 24 hours, so a shot the next morning still counts
    /// as the same event — reported as a folder "Verkaufsartikel im Laden" whose
    /// last picture was the dashboard of the car, taken the next day. At home
    /// and in the neighbourhood one day is one thing; on a trip it is not, and
    /// a holiday over midnight has to stay whole. Which of the two a place is
    /// has already been worked out (<see cref="Geo.PlaceIndex"/>, zones
    /// first).</para></summary>
    internal static List<MediaGroup> SplitEverydayPlacesByDay(
        List<MediaGroup> groups,
        double radiusKm,
        Geo.PlaceIndex places,
        IReadOnlyList<HomeZone> zones)
    {
        var result = new List<MediaGroup>();
        var extra = 0;

        foreach (var group in groups)
        {
            var days = group.Items.Select(i => i.TakenLocal.Date).Distinct().Count();
            if (group.IsDateless || days < 2
                || group.Centroid is not { } centre
                || !IsEverydayPlace(centre, radiusKm, places, zones))
            {
                result.Add(group);
                continue;
            }

            var parts = group.Items
                .GroupBy(i => i.TakenLocal.Date)
                .OrderBy(p => p.Key)
                .Select(p => new MediaGroup
                {
                    Items = [.. p.OrderBy(i => i.TakenLocal)],
                    Theme = group.Theme,
                })
                .ToList();

            result.AddRange(parts);
            extra += parts.Count - 1;
            RunLog.Log($"Everyday place: \"{group.Theme}\" with {group.Items.Count} shot(s) " +
                       $"split into {parts.Count} day(s).");
        }

        if (extra > 0)
        {
            RunLog.Log($"{extra} additional group(s) from cutting everyday places at the day boundary.");
        }

        return result;
    }

    /// <summary>Everyday place or travel — a zone outranks the visit count, in
    /// both directions.</summary>
    private static bool IsEverydayPlace(
        GeoPoint point, double radiusKm, Geo.PlaceIndex places, IReadOnlyList<HomeZone> zones)
    {
        foreach (var zone in zones)
        {
            if (zone.Covers(point))
            {
                return zone.Kind == ZoneKind.Home;
            }
        }

        return places.IsRoutine(point, radiusKm);
    }

    internal static List<MediaGroup> SplitByPause(
        List<MediaGroup> groups,
        TimeSpan maxGap)
    {
        if (maxGap <= TimeSpan.Zero)
        {
            return groups;
        }

        var result = new List<MediaGroup>();
        var extra = 0;

        foreach (var group in groups)
        {
            // Sammelordner und Alltagsordner sind Töpfe über die ganze
            // Sammlung, keine Ereignisse — sie haben keine Pausen, die etwas
            // bedeuten.
            if (group.IsDateless || group.Items.Count < 2)
            {
                result.Add(group);
                continue;
            }

            var parts = new List<List<MediaItem>>();
            List<MediaItem>? current = null;
            DateTime? previous = null;

            foreach (var item in group.Items.OrderBy(i => i.TakenLocal))
            {
                if (current is null || previous is not { } last || item.TakenLocal - last > maxGap)
                {
                    current = [];
                    parts.Add(current);
                }

                current.Add(item);
                previous = item.TakenLocal;
            }

            if (parts.Count <= 1)
            {
                result.Add(group);
                continue;
            }

            foreach (var part in parts)
            {
                result.Add(new MediaGroup
                {
                    Items = part,
                    Theme = group.Theme,
                    IsEveryday = group.IsEveryday,
                    IsUnnamed = group.IsUnnamed,
                    IsPeriodScoped = group.IsPeriodScoped,
                });
            }

            extra += parts.Count - 1;
        }

        if (extra > 0)
        {
            RunLog.Log($"{extra} additional group(s) from splitting at pauses longer than " +
                       $"{maxGap.TotalDays:0.#} day(s) within one place.");
        }

        return result;
    }

    /// <summary>Joins the clusters of one group that sit at the same place
    /// again, and puts each result back into time order.</summary>
    private static List<List<MediaItem>> MergeSamePlaceClusters(
        List<List<MediaItem>> clusters, double radiusKm)
    {
        var merged = new List<List<MediaItem>>();
        foreach (var cluster in clusters)
        {
            var centre = CentreOf(cluster);
            var target = centre is null
                ? null
                : merged.FirstOrDefault(m => CentreOf(m) is { } c && c.DistanceKm(centre.Value) <= radiusKm);

            if (target is null)
            {
                merged.Add(cluster);
            }
            else
            {
                target.AddRange(cluster);
            }
        }

        foreach (var cluster in merged)
        {
            cluster.Sort((x, y) => x.TakenLocal.CompareTo(y.TakenLocal));
        }

        return merged;
    }

    private static GeoPoint? CentreOf(List<MediaItem> items)
    {
        var located = items.Where(i => i.Location is not null).Select(i => i.Location!.Value).ToList();
        return located.Count == 0
            ? null
            : new GeoPoint(located.Average(p => p.Latitude), located.Average(p => p.Longitude));
    }

    /// <summary>EXIF-only mode: takes the shots without coordinates out of every
    /// group.
    ///
    /// <para>Without the model a group is a stretch of TIME and nothing else —
    /// the grouping never asks where anything was taken. The name, however,
    /// comes from the place of the shots that DO carry coordinates, and
    /// everything else in the same window inherited it. Reported case: a folder
    /// "20260725_Beispielstadt an der Alb" held pictures from a race track 400 km
    /// away. Of 271 shots in that window 60 had GPS, 28 of them near Beispielstadt,
    /// and those 28 named the folder for the other 243 — messenger images,
    /// screenshots and videos, none of which carries coordinates.</para>
    ///
    /// <para>What is taken out has no place, so nothing can name it, and it ends
    /// up in the collecting folder. That is deliberately a lot: of one measured
    /// collection 5017 of 6039 files have no GPS at all. Without coordinates and
    /// without the model there is simply no evidence of where a shot belongs —
    /// and a folder name that pretends otherwise is worse than a big collecting
    /// folder. The model modes are where content can tell them apart.</para></summary>
    /// <param name="collectUnmatched">With the collecting folder switched on the
    /// shots go straight into it — they can never be named by place, and a
    /// detour through naming would only hand them a place they do not have.
    /// Switched off they keep a group of their own.</param>
    internal static List<MediaGroup> SeparateUnlocatedShots(
        List<MediaGroup> groups,
        bool collectUnmatched,
        string unmatchedName)
    {
        var result = new List<MediaGroup>();
        var collection = groups.FirstOrDefault(g => g.IsCollection);
        var separated = 0;
        var touched = 0;

        foreach (var group in groups)
        {
            if (group.IsCollection)
            {
                result.Add(group);
                continue;
            }

            var unlocated = group.Items.Where(i => i.Location is null).ToList();
            if (unlocated.Count == 0)
            {
                result.Add(group);
                continue;
            }

            if (unlocated.Count < group.Items.Count)
            {
                result.Add(new MediaGroup
                {
                    Items = [.. group.Items.Where(i => i.Location is not null)],
                    Theme = group.Theme,
                    IsEveryday = group.IsEveryday,
                    IsUnnamed = group.IsUnnamed,
                    IsPeriodScoped = group.IsPeriodScoped,
                });
            }

            if (collectUnmatched)
            {
                if (collection is null)
                {
                    collection = new MediaGroup
                    {
                        Items = [], IsCollection = true, Theme = unmatchedName,
                    };
                    result.Add(collection);
                }

                collection.Items.AddRange(unlocated);
            }
            else
            {
                result.Add(new MediaGroup { Items = unlocated });
            }

            separated += unlocated.Count;
            touched++;
        }

        if (separated > 0)
        {
            collection?.Items.Sort((x, y) => x.TakenLocal.CompareTo(y.TakenLocal));
            RunLog.Log($"{separated} shot(s) without GPS taken out of {touched} group(s): " +
                       "a place name may only cover shots that carry that place.");
        }

        return result;
    }

    /// <summary>Gathers everything shot inside a home zone into one dateless
    /// folder per zone.
    ///
    /// <para>Without the model there is nothing that separates one day at home
    /// from the next — a folder per day would carry the date and nothing else,
    /// which the file names already say. So they all go into one folder named
    /// after the zone, and the folders that remain are the ones a place name
    /// can actually describe.</para>
    ///
    /// <para>Only zones of kind <see cref="ZoneKind.Home"/> count; a holiday
    /// flat on the map is the opposite of everyday life. Shots without GPS stay
    /// out, one by one and not merely group by group: nothing shows them to be
    /// at home, and dropping them in would quietly bury them.</para></summary>
    internal static List<MediaGroup> MergeEverydayGroups(
        List<MediaGroup> groups,
        IReadOnlyList<HomeZone> zones,
        List<RunWarning> warnings)
    {
        if (!zones.Any(z => z.Kind == ZoneKind.Home))
        {
            return groups;
        }

        var result = new List<MediaGroup>();
        var everyday = new Dictionary<string, MediaGroup>(StringComparer.OrdinalIgnoreCase);
        var gathered = 0;
        var leftBehind = 0;

        var homeZones = zones.Where(z => z.Kind == ZoneKind.Home).ToList();

        foreach (var group in groups.OrderBy(g => g.StartLocal))
        {
            if (group.IsCollection)
            {
                result.Add(group);
                continue;
            }

            // JEDE Aufnahme wird einzeln gefragt, nicht die Gruppe.
            //
            // Vorher entschied der Schwerpunkt: Lag der in der Zone, wanderte
            // die ganze Gruppe hinein. Da SplitByPlace vorher auf den
            // GPS-Radius bündelt (2 km), zog das alles im Umkreis mit — der
            // Spielplatz, der Supermarkt, gemeldet bei einer Zone von 100 m.
            // Eine Zone sagt aber genau, wie weit sie reicht; sie zu setzen und
            // dann den Bündelradius entscheiden zu lassen, ist keine Antwort
            // auf das, was der Nutzer eingestellt hat.
            var inZone = group.Items
                .Where(i => i.Location is { } location
                            && homeZones.FirstOrDefault(z => z.Covers(location)) is not null)
                .ToList();

            if (inZone.Count == 0)
            {
                result.Add(group);
                continue;
            }


            // Die Zone der MEISTEN Aufnahmen benennt den Ordner — bei zwei
            // Heimatzonen dicht beieinander sonst eine Frage der Reihenfolge.
            var zoneName = inZone
                .GroupBy(i => homeZones.First(z => z.Covers(i.Location!.Value)).Name)
                .OrderByDescending(g => g.Count())
                .First().Key;

            if (!everyday.TryGetValue(zoneName, out var target))
            {
                everyday[zoneName] = target = new MediaGroup
                {
                    Items = [],
                    Theme = zoneName,
                    IsEveryday = true,
                };
                result.Add(target);
            }

            target.Items.AddRange(inZone);
            gathered++;

            // Was übrig bleibt, behält seine Gruppe: Aufnahmen ohne GPS und
            // solche, die zwar Koordinaten tragen, aber außerhalb der Zone
            // liegen. Ohne Modell kann nichts davon einen Ortsnamen bekommen,
            // wenn kein Ort dahintersteht — dann landet es im Sammelordner.
            var rest = group.Items.Except(inZone).ToList();
            if (rest.Count > 0)
            {
                // Das Thema bleibt am Rest hängen. Ohne Modell ist es ohnehin
                // das Standardthema; mit Modell wäre es sonst weg, und was das
                // Modell an dieser Gruppe erkannt hat, müsste neu erfragt
                // werden.
                result.Add(new MediaGroup { Items = rest, Theme = group.Theme });
                leftBehind += rest.Count(i => i.Location is null);
            }
        }

        if (gathered == 0)
        {
            return result;
        }

        foreach (var group in everyday.Values)
        {
            group.Items.Sort((x, y) => x.TakenLocal.CompareTo(y.TakenLocal));
            RunLog.Log($"Everyday folder \"{group.Theme}\": {group.Items.Count} shot(s) " +
                       "from inside the home zone, no date prefix.");
        }

        if (leftBehind > 0)
        {
            RunLog.Log($"{leftBehind} shot(s) without GPS stayed out of the everyday folder(s): " +
                       "they shared a time window with shots taken at home, nothing more.");
        }

        // The folders are named, not just counted. A zone counts as home
        // unless its kind says otherwise — and "Home" is the default, so a
        // zone added in passing swallows everything shot there. Reading
        // "Urlaub (1168)" here says at once which zone that was.
        var listed = string.Join(", ", everyday.Values
            .OrderByDescending(g => g.Items.Count)
            .Select(g => $"\"{g.Theme}\" ({g.Items.Count})"));
        warnings.Add(RunWarnings.Everyday(gathered, listed));
        return result;
    }

    /// <summary>Führt zeitlich benachbarte Gruppen zusammen, die am selben Ort
    /// entstanden sind. Das LLM zerlegt eine Reise gern nach Tätigkeit
    /// ("Stadtbesichtigung", "Mittelaltermarkt", "Historischer Umzug") — für
    /// den Nutzer ist das EIN Ausflug, und aus einem Wochenende werden sonst
    /// fünf Ordner.
    ///
    /// <para>Greift nur an Orten, die die Sammlung EINMAL zeigt — also auf
    /// Reisen und Ausflügen. An Alltagsorten (zuhause, Großeltern) sind
    /// Spielen, Essen und Küchenumbau drei Ereignisse und keine drei Etappen;
    /// die würden sonst zu einem Klumpen verschmelzen.</para>
    ///
    /// <para>Wer eine ZONE gesetzt hat, hat die Frage bereits beantwortet: Eine
    /// Zone der Art <see cref="ZoneKind.Home"/> ist Alltag, eine der Art
    /// <see cref="ZoneKind.Vacation"/> ist es ausdrücklich nicht — auch dann
    /// nicht, wenn die Sammlung den Ort mehrfach zeigt. Genau dafür gibt es die
    /// Zonenart; sie hier zu übergehen hieße, den Nutzer nach etwas zu fragen
    /// und die Antwort wegzuwerfen.</para>
    ///
    /// <para>Wo keine Zone liegt, entscheidet <see cref="Geo.PlaceIndex"/> aus
    /// den Daten. Diese Reihenfolge ist der Kern: Die frühere Fassung prüfte NUR
    /// gegen die Zonen — lag so eine handgesetzte Koordinate zwei Kilometer
    /// daneben, galt das eigene Wohnzimmer als Ausflug und ein ganzer
    /// Alltagstag verschmolz zu einer Gruppe.</para></summary>
    internal static List<MediaGroup> MergeSameRegion(
        List<MediaGroup> groups,
        double radiusKm,
        TimeSpan maxGap,
        Geo.PlaceIndex places,
        IReadOnlyList<HomeZone>? zones = null)
    {
        bool IsEveryday(GeoPoint point)
        {
            // Zone schlägt Datenlage — in beide Richtungen.
            foreach (var zone in zones ?? [])
            {
                if (zone.Covers(point))
                {
                    return zone.Kind == ZoneKind.Home;
                }
            }

            return places.IsRoutine(point, radiusKm);
        }

        if (radiusKm <= 0 || groups.Count < 2)
        {
            return groups;
        }

        var result = new List<MediaGroup>();
        var merged = 0;
        foreach (var group in groups.OrderBy(g => g.StartLocal))
        {
            if (group.IsDateless || group.Centroid is not { } b || IsEveryday(b))
            {
                result.Add(group);
                continue;
            }

            // Bewusst gegen ALLE Gruppen in zeitlicher Reichweite prüfen, nicht
            // nur gegen die unmittelbar vorhergehende: Zwischen zwei Etappen
            // desselben Ausflugs kann eine Gruppe an einem anderen Ort liegen
            // (Altstadt – Campingplatz – Altstadt). Ein Vergleich nur mit dem
            // direkten Vorgänger ließe die beiden Altstadt-Gruppen für immer
            // getrennt. Genommen wird die räumlich nächste Gruppe.
            MediaGroup? best = null;
            var bestDistance = double.MaxValue;
            foreach (var candidate in result)
            {
                if (candidate.IsDateless || candidate.Centroid is not { } a
                    || IsEveryday(a)
                    || group.StartLocal - candidate.EndLocal > maxGap)
                {
                    continue;
                }

                var distance = a.DistanceKm(b);
                if (distance <= radiusKm && distance < bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            if (best is not null)
            {
                RunLog.Log($"Region merge: \"{group.Theme}\" ({group.Items.Count}) united with " +
                           $"\"{best.Theme}\" ({best.Items.Count}) — " +
                           $"{bestDistance:F1} km apart.");
                best.Items.AddRange(group.Items);
                best.Items.Sort((x, y) => x.TakenLocal.CompareTo(y.TakenLocal));
                merged++;
                continue;
            }

            result.Add(group);
        }

        if (merged > 0)
        {
            RunLog.Log($"{merged} neighbouring group(s) united within {radiusKm:0.#} km.");
        }

        return result;
    }


    /// <summary>Schiebt Gruppen unterhalb der Mindestgröße in den Sammelordner,
    /// statt für eine Handvoll Aufnahmen je einen eigenen Ordner anzulegen.
    /// <para>Der Sammelordner entsteht dafür auch dann, wenn die Option
    /// "Sammelordner für Unzuordenbares" aus ist — sonst gäbe es kein Ziel für
    /// die Aufnahmen. Die Warnung weist darauf hin.</para></summary>
    /// <param name="keepLocated">Groups with coordinates of their own survive
    /// below the minimum — <b>from two shots on</b>. Without the model GPS is
    /// the only evidence there is: two shots from the playground are a visit to
    /// the playground. A SINGLE shot is no event however precise its place. With
    /// the model, content is evidence too, so the configured minimum counts as
    /// written.</param>
    internal static List<MediaGroup> MergeUndersizedGroups(
        List<MediaGroup> groups, int minimum, bool keepLocated = false)
    {
        if (minimum <= 1)
        {
            return groups;
        }

        var undersized = groups
            .Where(g => !g.IsDateless && g.Items.Count < minimum
                        && !(keepLocated && g.Centroid is not null && g.Items.Count >= 2))
            .ToList();
        if (undersized.Count == 0)
        {
            return groups;
        }

        var remaining = groups.Except(undersized).ToList();
        var collection = remaining.FirstOrDefault(g => g.IsCollection);
        if (collection is null)
        {
            collection = new MediaGroup { Items = [], IsCollection = true };
            remaining.Add(collection);
        }

        var moved = 0;
        foreach (var group in undersized)
        {
            collection.Items.AddRange(group.Items);
            moved += group.Items.Count;
            RunLog.Log($"Group \"{group.Theme}\" with {group.Items.Count} shot(s) below the " +
                       $"minimum of {minimum} — moved into the collecting folder.");
        }

        RunLog.Log($"{undersized.Count} group(s) below the minimum: " +
                   $"{moved} shot(s) in the collecting folder.");
        return remaining;
    }

    /// <summary>Kleines JPEG einer Aufnahme für die Notiz-Vorschau; null, wenn
    /// sich die Datei nicht aufbereiten lässt.</summary>
    private byte[]? CreateThumbnail(MediaItem item)
    {
        try
        {
            return item.Kind == MediaKind.Video
                ? imagePreprocessor.GetVideoFrameJpeg(item.SourcePath, 320)
                : imagePreprocessor.GetJpegForAnalysis(item.SourcePath, 320);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            // Die Vorschau ist Komfort — die Notiz selbst zählt.
            return null;
        }
    }

    private OrganizePlan EmptyPlan(List<RunWarning> warnings) => new()
    {
        BaseFolder = settings.TargetFolder,
        Hierarchy = settings.TargetHierarchy,
        Mode = settings.TransferMode,
        Warnings = warnings,
    };

    /// <summary>Erkennt bekannte Personen auf allen Bildern und hinterlegt
    /// die Namen am Item — sie gehen später als gesicherte Tatsache in den
    /// Bild-Prompt ein. Videos werden übersprungen (dort liefert erst die
    /// Frame-Extraktion ein Bild, was den Nutzen nicht rechtfertigt).</summary>
    private static void RecognizePeople(
        List<MediaItem> items,
        PersonStore personStore,
        FaceModelProvider faceModels,
        OutputLanguage language,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        // Einmal vorab bauen: Fehlen oder klemmen die Modelle, soll das EINMAL
        // als Warnung erscheinen und nicht als Fehler an jedem einzelnen Bild.
        FaceService probe;
        try
        {
            probe = new FaceService(faceModels);
        }
        catch (Exception ex)
        {
            RunLog.Warn(RunWarnings.FacesUnavailable(ex.Message));
            return;
        }

        var images = items.Where(i => i.Kind == MediaKind.Image).ToList();
        using var phase = RunLog.Phase("Face recognition");
        phase.Units = images.Count;

        var cache = FaceCache.Load();

        // Die Netz-Objekte sind zustandsbehaftet und serialisieren jeden
        // Aufruf — geteilt brächte Parallelität also gar nichts. Deshalb je
        // Arbeiter eine eigene Instanz, ausgeliehen aus einem Vorrat.
        var pool = new ConcurrentBag<FaceService>();
        pool.Add(probe);

        // Bewusst nicht alle Kerne: OpenCV rechnet innerhalb eines Aufrufs
        // selbst schon mehrfädig, und das Dekodieren großer JPEGs sättigt die
        // Platte. Zu viele Arbeiter nehmen sich gegenseitig die Kerne weg.
        var parallel = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        var hits = 0;
        var cached = 0;
        var done = 0;

        try
        {
            Parallel.ForEach(
                images,
                new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct },
                item =>
                {
                    try
                    {
                        var (faces, fromCache) = DetectOrRecall(item, pool, faceModels, cache);
                        if (fromCache)
                        {
                            Interlocked.Increment(ref cached);
                        }

                        item.DetectedFaceCount = faces.Count;

                        foreach (var face in faces.OrderBy(f => f.RelativeCenterX))
                        {
                            var position = face.PositionHint(language);
                            // The strict bar: this name goes to the model as an
                            // established fact and ends up in a folder name.
                            // The assignment dialog keeps the generous one.
                            var match = FaceService.Match(
                                face.Embedding, personStore.Persons, FaceService.FactMatchThreshold);
                            if (match is not null && !item.RecognizedPeople.Contains(match.Person.Name))
                            {
                                item.RecognizedPeople.Add(match.Person.Name);
                                item.RecognizedPeopleWithPosition.Add($"{match.Person.Name} ({position})");
                                Interlocked.Increment(ref hits);
                                RunLog.Log($"{item.FileName}: {match.Person.Name} erkannt " +
                                           $"({position}, Ähnlichkeit {match.Similarity:F3})");
                            }
                            else
                            {
                                item.UnidentifiedPositions.Add(position);
                            }
                        }

                        if (item.UnidentifiedPositions.Count > 0)
                        {
                            RunLog.Log($"{item.FileName}: {item.UnidentifiedPositions.Count} " +
                                       $"nicht zugeordnete(s) Gesicht(er) ({string.Join(", ", item.UnidentifiedPositions)}).");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        RunLog.Log($"{item.FileName}: Gesichtserkennung fehlgeschlagen: {ex.Message}");
                    }

                    var count = Interlocked.Increment(ref done);
                    if (count % 10 == 0 || count == images.Count)
                    {
                        progress.Report(new PipelineProgress(
                            PipelinePhase.Faces, count, images.Count,
                            ProgressKeys.RecognizingFaces, count, images.Count));
                    }
                });
        }
        finally
        {
            cache.Save();
            foreach (var service in pool)
            {
                service.Dispose();
            }
        }

        RunLog.Log($"Gesichtserkennung abgeschlossen: {hits} Zuordnung(en) auf {images.Count} Bildern, " +
                   $"{cached} davon aus dem Cache ({parallel} gleichzeitig).");
    }

    /// <summary>Gesichter einer Aufnahme — aus dem Cache oder frisch erkannt.
    /// <para>Gespeichert werden nur die Merkmalsvektoren; wer wer ist, wird
    /// bei jedem Lauf neu bestimmt. Sonst würde eine neu angelegte Person auf
    /// alten Aufnahmen nie gefunden.</para></summary>
    private static (List<FaceSignature> Faces, bool FromCache) DetectOrRecall(
        MediaItem item,
        ConcurrentBag<FaceService> pool,
        FaceModelProvider faceModels,
        FaceCache cache)
    {
        var key = FaceCache.ComputeKey(item.SourcePath);
        if (key is not null && cache.TryGet(key) is { } known)
        {
            return (known, true);
        }

        if (!pool.TryTake(out var service))
        {
            service = new FaceService(faceModels);
        }

        List<FaceSignature> faces;
        try
        {
            faces = [.. service.DetectFaces(item.SourcePath)
                .Select(f => new FaceSignature(f.Embedding, f.RelativeCenterX))];
        }
        finally
        {
            pool.Add(service);
        }

        if (key is not null)
        {
            cache.Store(key, faces);
        }

        return (faces, false);
    }

    /// <summary>Liest die Metadaten aller Dateien parallel.
    ///
    /// <para>Vorher lief das seriell, mit einem eigenen <c>Task.Run</c> je
    /// Datei — 10 000 Sprünge in den Thread-Pool, die trotzdem nacheinander
    /// abliefen. Die Arbeit ist überwiegend Warten auf die Platte und lässt
    /// sich verlustfrei verteilen; die Reihenfolge bleibt erhalten, weil jedes
    /// Ergebnis an seinen Platz im Feld geschrieben wird.</para></summary>
    private static async Task<List<MediaItem>> ReadMetadataAsync(
        List<(string Path, MediaKind Kind)> files,
        IProgress<PipelineProgress> progress,
        CancellationToken ct)
    {
        using var phase = RunLog.Phase("Metadata");
        phase.Units = files.Count;

        var reader = new MediaMetadataReader();
        var results = new MediaItem[files.Count];
        var done = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct,
            },
            (index, token) =>
            {
                token.ThrowIfCancellationRequested();
                var (path, kind) = files[index];
                results[index] = reader.Read(path, kind);

                // Nicht für jede Datei melden: Bei 10 000 Dateien wären das
                // 10 000 Sprünge auf den UI-Thread, die mehr kosten als das
                // Lesen selbst.
                var count = Interlocked.Increment(ref done);
                if (count % 25 == 0 || count == files.Count)
                {
                    progress.Report(new PipelineProgress(
                        PipelinePhase.Metadata, count, files.Count,
                        ProgressKeys.ReadingMetadata, count, files.Count));
                }

                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        return [.. results];
    }

    private List<(string Path, MediaKind Kind)> ScanSourceFolder(List<RunWarning> warnings)
    {
        var source = Path.GetFullPath(settings.SourceFolder);
        var target = string.IsNullOrWhiteSpace(settings.TargetFolder)
            ? null
            : Path.GetFullPath(settings.TargetFolder);

        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var files = new List<(string, MediaKind)>();

        // Which extensions, not just how many: "12 skipped" leaves the user
        // guessing whether photos were lost, and the answer is in the list.
        var skippedExtensions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(source, "*", enumeration))
        {
            // Liegt der Zielordner im Quellordner, nicht erneut einsammeln.
            if (target is not null
                && path.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (MediaMetadataReader.Classify(path) is { } kind)
            {
                files.Add((path, kind));
                continue;
            }

            // Sidecars and marker files are skipped without a word — they were
            // never media, and counting them alarms the user for nothing.
            if (MediaMetadataReader.IsCompanionFile(path))
            {
                continue;
            }

            var extension = Path.GetExtension(path);
            extension = string.IsNullOrEmpty(extension) ? "(no extension)" : extension.ToLowerInvariant();
            skippedExtensions[extension] = skippedExtensions.GetValueOrDefault(extension) + 1;
        }

        if (skippedExtensions.Count > 0)
        {
            var listed = skippedExtensions
                .OrderByDescending(e => e.Value)
                .Take(6)
                .Select(e => $"{e.Key} ({e.Value})");
            warnings.Add(RunWarnings.Skipped(
                skippedExtensions.Values.Sum(), string.Join(", ", listed)));
        }

        return files;
    }
}
