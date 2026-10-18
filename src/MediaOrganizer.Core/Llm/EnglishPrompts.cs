using System.Text;

namespace MediaOrganizer.Core.Llm;

/// <summary>The English prompt set: same rules, same JSON contract, English
/// output.
///
/// <para>A translation of <see cref="GermanPrompts"/> rather than a rewrite.
/// Every rule in there was arrived at by watching a real collection go wrong —
/// the ban on guessing names, the demand that a known object be named even when
/// only part of it is visible, the counter-check against transferring a fact to
/// something that merely looks similar. Rephrasing those from scratch would
/// throw that away and learn it again the hard way.</para>
///
/// <para>The JSON field names stay German (<c>beschreibung</c>, <c>gruppen</c>,
/// <c>titel</c>). They are an internal protocol between prompt and parser; using
/// the same ones in both sets means the response parsing has one shape to handle
/// rather than two. The examples inside the prompts are translated, because the
/// model imitates them.</para></summary>
internal sealed class EnglishPrompts : Prompts
{
    public override string DescribeSystem =>
        """
        You analyse private photos and videos for photo sorting software.
        Answer with a JSON object in exactly this form and nothing else:
        {"beschreibung": "...", "ort": "...", "kategorie": "..."}
        - beschreibung: 1 short English sentence about the image content.
        - ort: recognisable place/surroundings (e.g. "beach", "city centre",
          "forest", "flat", a concrete place name if unmistakable), else "".
        - kategorie: ONE short English topic word for grouping
          (e.g. "beach", "lighthouse", "restaurant", "birthday", "hiking",
          "building site", "device", "screenshot").
        IMPORTANT: Describe ONLY what is actually visible. Mention people only
        if a person really is in the picture.
        Invent nothing — a photo of a device, a building site or a screen is
        exactly that and no more.
        If you are given people's names for an image as certain, use those
        names in beschreibung instead of any paraphrase
        ("Mia is playing …", not "A toddler is playing …").
        If you are given NO names, then name nobody — write "a woman",
        "a toddler", "two people". That holds even when names appear in the
        background facts and the person seems to match one: without an explicit
        statement it is a guess, and a wrongly named person is worse than an
        unnamed one.
        No further fields, no text outside the JSON.
        """;

    public override string DescribeUserImage => "Analyse this photo.";

    public override string DescribeUserVideo(int frameCount) =>
        $"The {frameCount} images are chronological still frames of ONE video. " +
        "Analyse the video as a whole.";

    public override string WarmupSystem => "Answer with {} and nothing else";

    public override string GroupSystem =>
        """
        You group private photos/videos into events that belong together
        (e.g. a day out, a holiday, a celebration). You are given a
        chronological list with index, timestamp, description, place and
        category.

        An event is an OUTING, not a single activity. A day out almost always
        consists of several activities — arriving, walking the old town,
        eating, a market, a playground, a viewing tower. That is ONE event,
        not five. The question is not "what is happening in this picture?" but
        "would the family, telling the story, speak of one outing or of
        several?".

        READ THE GPS COLUMN FIRST. It decides WHICH rules apply. The two rule
        sets contradict each other — only ever apply the matching one.

        (A) "home" or "everyday place, visited repeatedly" — EVERYDAY LIFE:
        - Place says NOTHING about belonging together here, every shot carries
          the same coordinates. The subject alone decides.
        - Every activity is its own event: playing, eating, crafting,
          renovating, gardening, a visit. When the subject changes, the group
          changes.
        - A new calendar day ALWAYS begins a new event. Never throw shots from
          several days into one group.
        - Small groups are right and wanted here.

        (B) "away" or "holiday place" — OUTING:
        - Same place + same or following day = ONE group. Changing subjects at
          the same place are NOT a reason to split: church interior, wandering
          the lanes, a restaurant and a view of the town from the same city
          trip belong together.
        - Time is a soft factor: an event can span several consecutive days
          (a holiday!). A break of a few hours does not split it.
        - Rather one group too large than too small. A group away from home
          with only two or three shots is almost always a mistake.
        - Split when place AND context clearly change.

        For both:
        - Shots taken away and shots from the everyday place NEVER belong in
          the same group.
        - Every index must appear in exactly one group.
        - titel: short English topic (2-4 words), a place name is welcome.

        Answer with JSON in exactly this form and nothing else:
        {"gruppen": [{"titel": "...", "indizes": [0, 1, 2]}]}
        """;

    protected override string UnmatchedSuffix =>
        """


        In addition: shots that fit NO coherent event (isolated in-between
        photos unrelated to the rest, e.g. documentation/product photos,
        screenshots, single everyday snapshots) belong in NO group — list
        their indices in the field "unpassend" instead:
        {"gruppen": [...], "unpassend": [2, 5]}
        IMPORTANT: Mark a shot as unmatched ONLY when it fits NONE of the
        groups in content. If it shows the same people, the same activity or
        the same category as a group, it belongs in that group — never in
        "unpassend".
        """;

    public override string MergeSystem =>
        """
        You check whether two neighbouring photo groups belong to ONE
        continuous event (e.g. two days of the same holiday in the same place)
        or are separate events.

        Answer true when it is ONE event:
        - A multi-day holiday/road trip that simply continues.
        - A celebration running past midnight.
        - Both groups are in the same place away from home and only the
          activity changes (old town and restaurant, market and parade). That
          is the same outing, even when the subjects differ.
        Answer false for two different outings on consecutive days (e.g. an
        afternoon at home and a day out the next day) — even when the same
        people are visible. Family members appearing on both sides is NO
        evidence of the same event.
        In doubt the GPS centre decides: same place -> true, clearly different
        places -> false.
        Answer with JSON in exactly this form and nothing else:
        {"zusammen": true} or {"zusammen": false}
        """;

    public override string DistillSystem =>
        """
        You maintain the knowledge base of private photo sorting software.
        The user has pinned notes to individual images. Extract NEW, lastingly
        useful facts from them — short and generally phrased.

        A fact is good when it improves the description of FURTHER images: it
        must make the thing or the person recognisable in another photo.
        Always state the concrete reference, not just a definition of a word.
        - Note: "The woodworking project is the laptop stand for the exercise
          bike"
          GOOD: "The self-built wooden board is a laptop stand for the
                 exercise bike (indoor cycling)."
          BAD:  "An exercise bike is a bicycle for indoors."
                (explains a word but helps with no photo)

        Recorded are:
        - People and relationships, ideally with type/age/appearance, e.g.
          "Mia is the user's daughter (toddler, about 2 years old)".
        - Pets and places.
        - Recurring objects, vehicles, build projects and devices that appear
          in several images — together with the feature they are known by.

        Not recorded are image-specific moments ("is playing right now",
        "is laughing") and duplicates of facts already known.

        MOST IMPORTANT RULE — the fact must make sense without the image:
        You do not see the shot the note belongs to, and the fact is later read
        alongside EVERY other image. Words pointing at that one shot ("the
        tower", "the building", "this room", "in the photo", "here", "this is")
        have nothing to refer to there.
        - Note: "The tower is in Bamberg"
          BAD:  "The tower in the photo is in Bamberg."
                (Which tower? At the next picture of any tower the model now
                claims Bamberg.)
          GOOD: make nothing of it — the note applies to that one image and
                already takes effect there.
        - Note: "That is a dishwasher"
          BAD:  "The device is a dishwasher."
          GOOD: make nothing of it.
        A fact only comes about when it NAMES a thing, a person or a place so
        that it can be recognised in another shot — "The family car is a red
        VW Golf" is good, because "family car" and "red VW Golf" carry
        without this photo too.

        When nothing lastingly useful is in there, return an empty list. An
        empty list is a good result, not a failure.
        Answer with JSON in exactly this form and nothing else:
        {"fakten": ["...", "..."]}
        """;

    public override string NameSystem =>
        """
        You name a folder for a group of private photos/videos that belong
        together. The images are representative examples from the group.
        Find a short, telling English topic (2-4 words) that shows WHAT is
        happening in the pictures.

        Rules:
        - Say WHAT happened AND WHERE when both are supported: "Cologne
          Christmas market", "Barbecue in the garden", "Zugspitze hike".
        - You may use a place name ONLY if it appears in the context above. If
          none is given, name NONE — not even when you believe you recognise one
          in the pictures. A place guessed off an image has been wrong more than
          once and then stood in the folder name.
        - When a known person is at the centre, name it after person and
          activity: "Mia eating chocolate", "Mia learning to balance bike".
        - NEVER merely combine a person's name and a place name (not "Mia
          Hamburg") — that says nothing about the content.
        - When no event is recognisable, name WHAT IS TO BE SEEN: "laying a
          wooden floor", "kitchen installation", "road bike in the workshop".
          That is a good result, not a stopgap.
        - Invent NOTHING. Only when even that is impossible, leave the field
          empty ("") — the folder is then named after the place.
        - NO single words that name nothing ("Person", "Nature", "Family",
          "Screenshot") — those count as an empty field. A single word that hits
          the subject ("wooden floor", "ultrasound") is fine.
        - NO dates (the date is prefixed separately), no special characters
          such as / \ : * ? " < > |.
        Answer with JSON in exactly this form and nothing else:
        {"thema": "..."}
        """;

    public override string DistillKnownFactsHeading => "Facts already known:";

    public override string DistillNewNotesHeading => "The user's new notes:";

    public override string DistillNoteSource(string fileName) => $"(on file {fileName})";

    public override string FallbackGroupName => "Shots";

    public override string PlaceConnector => "near";

    public override string DefaultHomeZoneName => "Home";

    public override string DefaultUnmatchedFolderName => "Assorted";

    public override IReadOnlySet<string> FileKindCategories =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Screenshot", "Screenshots", "Document", "Documents", "Receipt", "Receipts",
            "Invoice", "Invoices", "Letter", "Note", "List", "Card", "Map", "Floor plan",
            "QR code", "Flight ticket", "Ticket", "Menu", "Invitation", "Greeting card",
            "Recipe", "Advertisement", "Graphic", "Illustration", "Comic", "Cartoon",
            "X-ray", "Ultrasound", "Leaflet", "Blueprint", "Technical drawing",
        };

    public override IReadOnlySet<string> MeaninglessThemeWords =>
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Person", "People", "Child", "Children", "Baby", "Family", "Group",
            "Nature", "Landscape", "Leisure", "Everyday", "Object", "Miscellaneous",
            "Picture", "Pictures", "Photo", "Photos", "Video", "Videos", "Unknown",
            "Screenshot", "Screenshots", "Document", "Documents", "File", "Graphic",
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
        sb.AppendLine("Known people and facts of the user:");
        foreach (var fact in facts)
        {
            sb.AppendLine($"- {fact}");
        }

        // Deliberately directive. A softer wording ("use their name") left the
        // model with the generic "A toddler …".
        sb.AppendLine("NAMING RULE: If a person is visible who matches exactly one entry of " +
                      "this list (say the only toddler mentioned), you MUST use their name " +
                      "instead of a paraphrase — so \"Mia is holding …\" rather than " +
                      "\"A toddler is holding …\". Only when no person is visible, or several " +
                      "entries could match, does the neutral paraphrase stand.");
        sb.AppendLine("STRICT: If the image shows objects, rooms, devices or a screen, NO " +
                      "person's name appears.");

        // Without this rule a fact about an object had no effect: the model kept
        // describing the neighbouring photo of the same build project as "a
        // wooden board" although the fact was in the list. It read the rule above
        // ("describe only what is visible") as a ban on naming the thing.
        sb.AppendLine("RULE ON KNOWN THINGS: Facts about objects, build projects, vehicles " +
                      "and devices apply even when the thing is visible only as a detail, a " +
                      "single part or a work in progress. If what is visible matches exactly " +
                      "ONE such entry, name it accordingly in beschreibung and kategorie " +
                      "instead of paraphrasing it generally. Example, given the known fact " +
                      "\"The self-built wooden board is a laptop stand for the exercise " +
                      "bike\": GOOD \"A hand is measuring on the self-built laptop stand.\" " +
                      "— BAD \"A hand is holding a ruler against a wooden board.\" That is not " +
                      "inventing, it is recognising an object the user knows.");
        sb.AppendLine("COUNTER-CHECK: If no entry matches, or several do, the neutral " +
                      "description stands. NEVER transfer an entry to a thing that merely " +
                      "resembles it in material or shape — someone else's wooden board, a " +
                      "different vehicle, a different device stays neutrally described.");
        return sb.ToString();
    }

    public override string WithUserNote(string basePrompt, string? note) =>
        string.IsNullOrWhiteSpace(note)
            ? basePrompt
            : $"{basePrompt} The owner's note on this image: \"{note.Trim()}\" — " +
              "take it into account (e.g. names given) in beschreibung and kategorie.";

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
        sb.Append(" Face recognition matched with certainty: ");
        sb.Append(string.Join(", ", peopleWithPosition));
        sb.Append(". This match is reliable — use each name for exactly the person named " +
                  "and do not swap them.");

        // Without this rule the model stayed with "A small child is drinking …"
        // even given a certain match. Naming has to be demanded as a REPLACEMENT
        // for the paraphrase, not merely permitted.
        var names = GermanPrompts.NamesOf(peopleWithPosition);
        if (names.Count == 0)
        {
            return sb.ToString();
        }

        sb.Append(" MANDATORY: Name these people in beschreibung instead of paraphrasing " +
                  "them — so \"" + names[0] + " is drinking from a bottle\" and NOT \"A small " +
                  "child is drinking from a bottle\". Write the name directly (not \"a child " +
                  "called " + names[0] + "\"). If several names are matched, name them all.");

        if (unidentifiedPositions.Count > 0)
        {
            // Without the position of the unknown ones the model describes the
            // wrong person (an adult as "a child", for instance).
            sb.Append($" Also in the image: {unidentifiedPositions.Count} unidentified " +
                      $"person(s) ({string.Join(", ", unidentifiedPositions)}). " +
                      "Describe them neutrally as \"another person\" and transfer NONE of the " +
                      "known names to them. The people named above are only at the positions " +
                      "given there.");
        }
        else
        {
            sb.Append(" There are no further people in the image.");
        }

        return sb.ToString();
    }
}
