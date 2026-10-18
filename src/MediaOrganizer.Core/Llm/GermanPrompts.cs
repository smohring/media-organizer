using System.Text;

namespace MediaOrganizer.Core.Llm;

/// <summary>The German prompt set — the one this application shipped with, and
/// the default.
///
/// <para><b>Do not reword anything below without need.</b> Rewording a rule
/// means raising <c>MediaDescriber.PromptVersion</c>, and that expires every
/// stored description — hours of re-analysis on a real collection.</para>
///
/// <para>The names and places in the examples are invented. They used to be
/// the author's own, which put a real child's name and a real home town into
/// every shipped binary. Replacing them did not raise the prompt version on
/// purpose: an example name carries no rule, so descriptions produced with the
/// old ones are exactly as good and stay valid.</para></summary>
internal sealed class GermanPrompts : Prompts
{
    public override string DescribeSystem =>
        """
        Du analysierst private Fotos und Videos für eine Foto-Sortier-Software.
        Antworte ausschließlich mit einem JSON-Objekt exakt in dieser Form:
        {"beschreibung": "...", "ort": "...", "kategorie": "..."}
        - beschreibung: 1 kurzer deutscher Satz zum Bildinhalt.
        - ort: erkennbarer Ort/Umgebung (z.B. "Strand", "Innenstadt", "Wald",
          "Wohnung", konkreter Ortsname falls eindeutig erkennbar), sonst "".
        - kategorie: EIN kurzes deutsches Themenwort für Gruppierung
          (z.B. "Strand", "Leuchtturm", "Restaurant", "Geburtstag", "Wandern",
          "Baustelle", "Gerät", "Screenshot").
        WICHTIG: Beschreibe NUR, was tatsächlich sichtbar ist. Erwähne
        Personen ausschließlich, wenn wirklich eine Person zu sehen ist.
        Erfinde nichts hinzu — ein Foto eines Geräts, einer Baustelle oder
        eines Bildschirms ist genau das, nicht mehr.
        Werden dir zu einem Bild Personennamen als gesichert genannt, benutze
        diese Namen in der beschreibung anstelle jeder Umschreibung
        ("Mia spielt …", nicht "Ein Kleinkind spielt …").
        Werden dir KEINE Namen genannt, dann benenne auch niemanden — schreibe
        "eine Frau", "ein Kleinkind", "zwei Personen". Das gilt auch dann,
        wenn in den Hintergrund-Fakten Namen vorkommen und die Person zu ihnen
        zu passen scheint: Ohne ausdrückliche Nennung ist es eine Vermutung,
        und eine falsch benannte Person ist schlimmer als eine unbenannte.
        Keine weiteren Felder, kein Text außerhalb des JSON.
        """;

    public override string DescribeUserImage => "Analysiere dieses Foto.";

    public override string DescribeUserVideo(int frameCount) =>
        $"Die {frameCount} Bilder sind chronologische Standbilder EINES Videos. " +
        "Analysiere das Video als Ganzes.";

    public override string WarmupSystem => "Antworte ausschließlich mit {}";

    public override string GroupSystem =>
        """
        Du gruppierst private Fotos/Videos in zusammengehörige Ereignisse
        (z.B. ein Ausflug, ein Urlaub, eine Feier). Du bekommst eine
        chronologische Liste mit Index, Zeitstempel, Beschreibung, Ort und
        Kategorie.

        Ein Ereignis ist eine UNTERNEHMUNG, keine einzelne Tätigkeit. Ein
        Ausflug besteht fast immer aus mehreren Tätigkeiten — ankommen,
        Altstadt anschauen, essen, Markt, Spielplatz, Aussichtsturm. Das ist
        EIN Ereignis, nicht fünf. Die Frage lautet nicht "was passiert auf dem
        Bild?", sondern "würde die Familie beim Erzählen von einer oder von
        mehreren Unternehmungen sprechen?".

        ZUERST die GPS-Spalte lesen. Sie entscheidet, WELCHE Regeln gelten.
        Die beiden Regelsätze widersprechen einander — wende immer nur den
        passenden an.

        (A) "Zuhause" oder "Alltagsort, mehrfach besucht" — ALLTAG:
        - Der Ort sagt hier NICHTS über Zusammengehörigkeit aus, alle
          Aufnahmen tragen dieselben Koordinaten. Allein das Motiv entscheidet.
        - Jede Tätigkeit ist ein eigenes Ereignis: Spielen, Essen, Basteln,
          Umbau, Gartenarbeit, Besuch. Wechselt das Motiv, wechselt die Gruppe.
        - Ein neuer Kalendertag beginnt IMMER ein neues Ereignis. Wirf niemals
          Aufnahmen mehrerer Tage in eine Gruppe.
        - Kleine Gruppen sind hier richtig und erwünscht.

        (B) "unterwegs" oder "Urlaubsort" — UNTERNEHMUNG:
        - Gleicher Ort + gleicher oder Folgetag = EINE Gruppe. Wechselnde
          Motive am selben Ort sind KEIN Trenngrund: Kircheninnenraum,
          Gassenbummel, Restaurant und Stadtansicht desselben Städtetrips
          gehören zusammen.
        - Zeit ist ein weicher Faktor: Ein Ereignis kann mehrere
          zusammenhängende Tage umfassen (Urlaub!). Eine Pause von einigen
          Stunden trennt nicht.
        - Lieber eine Gruppe zu groß als zu klein. Eine auswärtige Gruppe mit
          nur zwei, drei Aufnahmen ist fast immer ein Fehler.
        - Trenne, wenn Ort UND Kontext klar wechseln.

        Für beide gilt:
        - Aufnahmen unterwegs und Aufnahmen vom Alltagsort gehören NIE in
          dieselbe Gruppe.
        - Jeder Index muss in genau einer Gruppe vorkommen.
        - titel: kurzes deutsches Thema (2-4 Wörter), gern mit Ortsname.

        Antworte ausschließlich mit JSON exakt in dieser Form:
        {"gruppen": [{"titel": "...", "indizes": [0, 1, 2]}]}
        """;

    protected override string UnmatchedSuffix =>
        """


        Zusätzlich: Aufnahmen, die zu KEINEM zusammenhängenden Ereignis
        passen (vereinzelte Zwischendurch-Fotos ohne Bezug zu den übrigen,
        z.B. Dokumentations-/Produktfotos, Screenshots, einzelne
        Alltags-Schnappschüsse), gehören in KEINE Gruppe — führe ihre
        Indizes stattdessen im Feld "unpassend" auf:
        {"gruppen": [...], "unpassend": [2, 5]}
        WICHTIG: Markiere eine Aufnahme NUR als unpassend, wenn sie zu
        KEINER der Gruppen inhaltlich passt. Zeigt sie dieselben Personen,
        dieselbe Aktivität oder dieselbe Kategorie wie eine Gruppe,
        gehört sie in diese Gruppe — niemals in "unpassend".
        """;

    public override string MergeSystem =>
        """
        Du prüfst, ob zwei zeitlich benachbarte Foto-Gruppen zu EINEM
        durchgehenden Ereignis gehören (z.B. zwei Tage desselben Urlaubs am
        selben Ort) oder getrennte Ereignisse sind.

        Antworte mit true, wenn es EIN Ereignis ist:
        - Mehrtägiger Urlaub/Roadtrip, der einfach weitergeht.
        - Eine Feier, die über Mitternacht läuft.
        - Beide Gruppen liegen am selben auswärtigen Ort und die Tätigkeit
          wechselt nur (Altstadt und Restaurant, Markt und Umzug). Das ist
          dieselbe Unternehmung, auch wenn die Motive verschieden sind.
        Antworte mit false bei zwei verschiedenen Unternehmungen an
        aufeinanderfolgenden Tagen (z.B. Zuhause-Nachmittag und Ausflug am
        Folgetag) — auch wenn dieselben Personen zu sehen sind. Dass Familien-
        mitglieder auf beiden Seiten vorkommen, ist KEIN Beleg für dasselbe
        Ereignis.
        Im Zweifel entscheidet der GPS-Schwerpunkt: derselbe Ort -> true,
        deutlich verschiedene Orte -> false.
        Antworte ausschließlich mit JSON exakt in dieser Form:
        {"zusammen": true} oder {"zusammen": false}
        """;

    public override string DistillSystem =>
        """
        Du pflegst die Wissensbasis einer privaten Foto-Sortier-Software.
        Der Nutzer hat Notizen an einzelne Bilder geheftet. Extrahiere daraus
        NEUE, dauerhaft nützliche Fakten — kurz und allgemeingültig formuliert.

        Ein Fakt ist dann gut, wenn er das Beschreiben WEITERER Bilder
        verbessert: Er muss das Ding oder die Person auf einem anderen Foto
        wiedererkennbar machen. Nenne dafür immer den konkreten Bezug, nicht
        nur eine Worterklärung.
        - Notiz: "Das Holzbauprojekt ist der Notebookhalter für den
          Heimtrainer"
          GUT:     "Das selbstgebaute Holzbrett ist ein Notebookhalter für
                    den Heimtrainer (Indoor-Radtraining)."
          SCHLECHT: "Ein Heimtrainer ist ein Fahrrad für drinnen."
                    (erklärt ein Wort, hilft aber bei keinem Foto weiter)

        Erfasst werden:
        - Personen und Beziehungen, möglichst mit Typ/Alter/Aussehen, z.B.
          "Mia ist die Tochter des Nutzers (Kleinkind, ca. 2 Jahre)".
        - Haustiere und Orte.
        - Wiederkehrende Gegenstände, Fahrzeuge, Bauprojekte und Geräte, die
          auf mehreren Bildern auftauchen — mitsamt dem Merkmal, an dem man
          sie erkennt.

        Nicht erfasst werden bildspezifische Augenblicke ("spielt gerade",
        "lacht") und Dopplungen zu bereits bekannten Fakten.

        WICHTIGSTE REGEL — der Fakt muss ohne das Bild verständlich sein:
        Du siehst die Aufnahme nicht, zu der die Notiz gehört, und der Fakt
        wird später bei JEDEM anderen Bild mitgelesen. Wörter, die auf die
        eine Aufnahme zeigen ("der Turm", "das Gebäude", "dieses Zimmer",
        "auf dem Foto", "hier", "das ist"), haben dort keinen Bezug mehr.
        - Notiz: "Der Turm befindet sich in Bamberg"
          SCHLECHT: "Der Turm auf dem Foto befindet sich in Bamberg."
                    (Welcher Turm? Beim nächsten Bild eines
                    beliebigen Turms behauptet das Modell nun Bamberg.)
          GUT:      nichts daraus machen — die Notiz gilt genau diesem Bild
                    und wirkt dort ohnehin schon.
        - Notiz: "Das ist eine Spülmaschine"
          SCHLECHT: "Das Gerät ist eine Spülmaschine."
          GUT:      nichts daraus machen.
        Ein Fakt entsteht nur, wenn er ein Ding, eine Person oder einen Ort
        BENENNT, sodass man es auf einer anderen Aufnahme wiedererkennt —
        "Das Familienauto ist ein roter VW Golf" ist gut, weil
        "Familienauto" und "roter VW Golf" auch ohne dieses Foto tragen.

        Wenn nichts dauerhaft Nützliches enthalten ist, liefere eine leere
        Liste. Eine leere Liste ist ein gutes Ergebnis, kein Versagen.
        Antworte ausschließlich mit JSON exakt in dieser Form:
        {"fakten": ["...", "..."]}
        """;

    public override string NameSystem =>
        """
        Du benennst einen Ordner für eine Gruppe zusammengehöriger privater
        Fotos/Videos. Die Bilder sind repräsentative Beispiele aus der Gruppe.
        Finde ein kurzes, sprechendes deutsches Thema (2-4 Wörter), das zeigt,
        WAS auf den Bildern passiert.

        Regeln:
        - Nenne WAS passiert ist UND WO, wenn beides belegt ist:
          "Weihnachtsmarkt Köln", "Grillabend im Garten", "Wanderung Zugspitze".
        - Einen Ortsnamen darfst du NUR verwenden, wenn er oben im Kontext
          steht. Steht dort keiner, nenne KEINEN — auch nicht, wenn du auf den
          Bildern einen zu erkennen glaubst. Ein Ort, der aus dem Bild geraten
          ist, war schon mehrfach falsch und stand dann im Ordnernamen.
        - Steht eine bekannte Person im Mittelpunkt, benenne nach Person und
          Aktivität: "Mia beim Schokolade essen", "Mia lernt Laufrad".
        - Kombiniere NIEMALS bloß Personenname und Ortsname (nicht
          "Mia Hamburg") — das sagt nichts über den Inhalt aus.
        - Ist kein Ereignis zu erkennen, benenne, WAS ZU SEHEN IST:
          "Holzboden verlegen", "Kücheneinbau", "Rennrad in der Werkstatt".
          Das ist ein gutes Ergebnis, kein Notbehelf.
        - Erfinde NICHTS. Nur wenn auch das nicht geht, lass das Feld leer ("") —
          der Ordner heißt dann nach dem Ort.
        - KEINE Einzelwörter, die nichts benennen ("Person", "Natur", "Familie",
          "Screenshot") — die zählen wie ein leeres Feld. Ein Einzelwort, das die
          Sache trifft ("Holzboden", "Ultraschall"), ist dagegen in Ordnung.
        - KEINE Datumsangaben (das Datum wird separat vorangestellt), keine
          Sonderzeichen wie / \ : * ? " < > |.
        Antworte ausschließlich mit JSON exakt in dieser Form:
        {"thema": "..."}
        """;

    public override string DistillKnownFactsHeading => "Bereits bekannte Fakten:";

    public override string DistillNewNotesHeading => "Neue Notizen des Nutzers:";

    public override string DistillNoteSource(string fileName) => $"(an Datei {fileName})";

    public override string FallbackGroupName => "Aufnahmen";

    public override string PlaceConnector => "bei";

    public override string DefaultHomeZoneName => "Zuhause";

    public override string DefaultUnmatchedFolderName => "Vermischt";

    public override IReadOnlySet<string> FileKindCategories =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Screenshot", "Screenshots", "Dokument", "Dokumente", "Beleg", "Belege",
            "Rechnung", "Rechnungen", "Brief", "Zettel", "Notiz", "Liste", "Karte",
            "Landkarte", "Lageplan", "QR-Code", "Flugticket", "Ticket", "Fahrkarte",
            "Speisekarte", "Einladung", "Grußkarte", "Rezept", "Werbung", "Grafik",
            "Illustration", "Comic", "Cartoon", "Röntgenbild", "Ultraschall",
            "Informationsbroschüre", "Bauplan", "Technische Zeichnung",
        };

    public override IReadOnlySet<string> MeaninglessThemeWords =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Person", "Personen", "Kind", "Kinder", "Baby", "Familie", "Gruppe",
            "Natur", "Landschaft", "Freizeit", "Alltag", "Gegenstand", "Sonstiges",
            "Bild", "Bilder", "Foto", "Fotos", "Video", "Videos", "Unbekannt",
            "Screenshot", "Screenshots", "Dokument", "Dokumente", "Datei", "Grafik",
        };

    public override string WithKnowledge(string baseSystem, IReadOnlyList<string> facts)
    {
        if (facts.Count == 0)
        {
            return baseSystem;
        }

        var sb = new StringBuilder(baseSystem);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Bekannte Personen und Fakten des Nutzers:");
        foreach (var fact in facts)
        {
            sb.AppendLine($"- {fact}");
        }

        // Deliberately directive. A softer wording ("use their name") left the
        // model with the generic "Ein Kleinkind …".
        sb.AppendLine("REGEL ZUR NAMENSNENNUNG: Ist eine Person zu sehen, die zu genau einem " +
                      "Eintrag dieser Liste passt (etwa das einzige genannte Kleinkind), MUSST " +
                      "du ihren Namen verwenden statt einer Umschreibung — also \"Mia hält …\" " +
                      "statt \"Ein Kleinkind hält …\". Nur wenn keine Person zu sehen ist oder " +
                      "mehrere Einträge passen könnten, bleibt es bei der neutralen Umschreibung.");
        sb.AppendLine("STRIKT: Zeigt das Bild Gegenstände, Räume, Geräte oder einen Bildschirm, " +
                      "kommt KEIN Personenname vor.");

        // Without this rule a fact about an object had no effect: the model kept
        // describing the neighbouring photo of the same build project as "eine
        // Holzplatte" although the fact was in the list. It read the rule above
        // ("describe only what is visible") as a ban on naming the thing.
        sb.AppendLine("REGEL ZU BEKANNTEN DINGEN: Fakten über Gegenstände, Bauprojekte, " +
                      "Fahrzeuge und Geräte gelten auch dann, wenn das Ding nur als Ausschnitt, " +
                      "Einzelteil oder Zwischenstand zu sehen ist. Passt das Sichtbare zu genau " +
                      "EINEM solchen Eintrag, benenne es in beschreibung und kategorie danach, " +
                      "statt es allgemein zu umschreiben. Beispiel bei bekanntem Fakt " +
                      "\"Das selbstgebaute Holzbrett ist ein Notebookhalter für den " +
                      "Heimtrainer\": GUT \"Eine Hand misst am selbstgebauten " +
                      "Notebookhalter.\" — SCHLECHT \"Eine Hand hält ein Lineal an einer " +
                      "Holzplatte.\" Das ist kein Hinzuerfinden, sondern das Wiedererkennen " +
                      "eines dem Nutzer bekannten Gegenstands.");
        sb.AppendLine("GEGENPROBE: Passt kein Eintrag oder passen mehrere, bleibt es bei der " +
                      "neutralen Beschreibung. Übertrage einen Eintrag NIE auf ein Ding, das " +
                      "ihm nur im Material oder in der Form ähnelt — eine fremde Holzplatte, " +
                      "ein anderes Fahrzeug, ein anderes Gerät bleibt neutral beschrieben.");
        return sb.ToString();
    }

    public override string WithUserNote(string basePrompt, string? note) =>
        string.IsNullOrWhiteSpace(note)
            ? basePrompt
            : $"{basePrompt} Hinweis des Besitzers zu diesem Bild: \"{note.Trim()}\" — " +
              "berücksichtige ihn (z.B. genannte Namen) in beschreibung und kategorie.";

    public override string WithRecognizedPeople(
        string basePrompt,
        IReadOnlyList<string> peopleWithPosition,
        IReadOnlyList<string> unidentifiedPositions)
    {
        if (peopleWithPosition.Count == 0)
        {
            return basePrompt;
        }

        var sb = new StringBuilder(basePrompt);
        sb.Append(" Per Gesichtserkennung wurde sicher zugeordnet: ");
        sb.Append(string.Join(", ", peopleWithPosition));
        sb.Append(". Diese Zuordnung ist verlässlich — verwende die Namen genau für die " +
                  "jeweils genannte Person und vertausche sie nicht.");

        // Without this rule the model stayed with "Ein kleines Kind trinkt …"
        // even given a certain match. Naming has to be demanded as a REPLACEMENT
        // for the paraphrase, not merely permitted.
        var names = NamesOf(peopleWithPosition);
        if (names.Count == 0)
        {
            return sb.ToString();
        }

        sb.Append(" PFLICHT: Nenne diese Person(en) in der beschreibung beim Namen, " +
                  "statt sie zu umschreiben — also \"" + names[0] + " trinkt aus einer Flasche\" " +
                  "und NICHT \"Ein kleines Kind trinkt aus einer Flasche\". Schreibe den Namen " +
                  "direkt (nicht \"ein Kind namens " + names[0] + "\"). " +
                  "Sind mehrere Namen zugeordnet, nenne sie alle.");

        if (unidentifiedPositions.Count > 0)
        {
            // Without the position of the unknown ones the model describes the
            // wrong person (an adult as "a child", for instance).
            sb.Append($" Außerdem im Bild: {unidentifiedPositions.Count} nicht identifizierte " +
                      $"Person(en) ({string.Join(", ", unidentifiedPositions)}). " +
                      "Beschreibe diese neutral als \"eine weitere Person\" und übertrage " +
                      "KEINEN der bekannten Namen auf sie. Die namentlich genannten Personen " +
                      "sind ausschließlich an den oben angegebenen Positionen.");
        }
        else
        {
            sb.Append(" Weitere Personen sind nicht im Bild.");
        }

        return sb.ToString();
    }

    /// <summary>Strips the position hint from "Mia (links)" so the name can be
    /// used inside a sentence.</summary>
    internal static List<string> NamesOf(IReadOnlyList<string> peopleWithPosition) =>
        [.. peopleWithPosition
            .Select(p => p.Contains('(') ? p[..p.IndexOf('(')].Trim() : p.Trim())
            .Where(n => n.Length > 0)
            .Distinct()];
}
