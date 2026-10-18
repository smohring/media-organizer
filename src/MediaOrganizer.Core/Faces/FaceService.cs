using MediaOrganizer.Core.Models;
using MediaOrganizer.Core.Services;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace MediaOrganizer.Core.Faces;

/// <summary>A face found in an image, together with its feature vector.</summary>
public sealed class DetectedFace
{
    /// <summary>Position in the original image, in pixels.</summary>
    public required Rect Box { get; init; }

    /// <summary>How confident the detector is, 0 to 1.</summary>
    public required float Score { get; init; }

    /// <summary>128-dimensional, L2-normalised feature vector from SFace. This
    /// is the compact, comparable representation of a face — about 500 bytes,
    /// whatever the image size.</summary>
    public required float[] Embedding { get; init; }

    /// <summary>The aligned face crop as a JPEG, for display in the knowledge
    /// view.</summary>
    public byte[]? CropJpeg { get; init; }

    /// <summary>Horizontal position of the face in the frame (0 = far left,
    /// 1 = far right).</summary>
    public required double RelativeCenterX { get; init; }

    /// <summary>Height of the face relative to the image height — a rough
    /// indication of foreground against background.</summary>
    public required double RelativeHeight { get; init; }

    /// <summary>The position given to the prompt, so the model puts names on
    /// the right people.</summary>
    public string PositionHint(OutputLanguage language) =>
        PositionHintFor(RelativeCenterX, language);

    /// <summary>The same division for cached faces
    /// (<see cref="FaceSignature"/>). It has to stay identical across runs, or
    /// a cache hit changes the prompt.
    ///
    /// <para><b>The German wording is frozen.</b> The hint goes into the prompt
    /// and therefore into the cache key; changing it expires every stored
    /// description of an image with a recognised person.</para></summary>
    public static string PositionHintFor(double relativeCenterX, OutputLanguage language) =>
        language == OutputLanguage.English
            ? relativeCenterX switch
            {
                < 0.33 => "on the left of the image",
                < 0.67 => "in the centre of the image",
                _ => "on the right of the image",
            }
            : relativeCenterX switch
            {
                < 0.33 => "links im Bild",
                < 0.67 => "in der Bildmitte",
                _ => "rechts im Bild",
            };
}

/// <summary>A face matched to a known person.</summary>
public sealed record PersonMatch(KnownPerson Person, double Similarity);

/// <summary>Face recognition through OpenCV DNN with YuNet (detection) and
/// SFace (feature vectors), both Apache-2.0 from the OpenCV Zoo.
///
/// <para>Per image: YuNet gives face boxes and five landmarks (eyes, nose,
/// mouth corners). From those points the crop is brought onto the ArcFace
/// standard pose at 112×112 by a similarity transform — that alignment step is
/// decisive, without it the vectors are useless. SFace turns the result into
/// 128 values; comparing two faces afterwards is a dot product, taking
/// microseconds regardless of how many people are known.</para>
///
/// <para>Not thread-safe: the network objects carry state, so all calls are
/// serialised.</para></summary>
public sealed class FaceService : IDisposable
{
    /// <summary>The cosine threshold OpenCV recommends for SFace. Good enough
    /// for a SUGGESTION in the assignment dialog, where the user decides and a
    /// wrong guess costs a click.</summary>
    public const double DefaultMatchThreshold = 0.363;

    /// <summary>What it takes to tell the language model a name as an
    /// established fact.
    ///
    /// <para>Reported case: a coffee mug was detected as a face (detector
    /// confidence 0.472 where real faces score 0.87–0.92) and matched "Ben"
    /// at 0.417. That name went into the image prompt as a fact, the model
    /// wrote "Ben hält Anna auf dem Schoß", and the folder was named after
    /// it. Measured on the same collection, a real match scores 0.581.</para>
    ///
    /// <para>The dialog keeps the lower bar on purpose: a weak find there is
    /// one line to dismiss, while a weak find in the prompt becomes a sentence
    /// nobody can trace back.</para></summary>
    public const double FactMatchThreshold = 0.50;

    /// <summary>The best match has to beat the second best by this much —
    /// otherwise the assignment counts as ambiguous, which is exactly the case
    /// of two similar-looking people.</summary>
    private const double AmbiguityMargin = 0.05;

    /// <summary>Longest edge used for detection. Larger barely finds more
    /// faces and costs noticeable time.</summary>
    private const int DetectionSize = 640;

    private static readonly int[] Strides = [8, 16, 32];

    /// <summary>The ArcFace reference pose for 112×112 (eyes, nose, mouth
    /// corners) — the same template OpenCV's own SFace implementation
    /// uses.</summary>
    private static readonly Point2f[] AlignmentTemplate =
    [
        new(38.2946f, 51.6963f),
        new(73.5318f, 51.5014f),
        new(56.0252f, 71.7366f),
        new(41.5493f, 92.3655f),
        new(70.7299f, 92.2041f),
    ];

    private readonly object _sync = new();
    private readonly Net _detector;
    private readonly Net _recognizer;
    private readonly float _scoreThreshold;
    private bool _disposed;

    /// <param name="scoreThreshold">The confidence at which the detector
    /// reports a face.
    ///
    /// <para>This value is measured, not guessed — on a burst of four shots of
    /// the same scene (an adult with a toddler on their shoulders, a busy
    /// lane). The adult scored 0.825 / 0.841 / <b>0.724</b> / 0.831: the
    /// earlier threshold of 0.75 cut exactly one shot away, and in the "assign
    /// people" dialog there was no face there at all. The toddler scored
    /// 0.486 / 0.540 / 0.198 / 0.433 — at 0.6 they would have been found on
    /// NONE of the four, even though their identity is unmistakable at
    /// 0.67–0.73 once the face is there.</para>
    ///
    /// <para>0.40 takes in every find that carried a real identity in the
    /// measurement and leaves out the noise below it (confidence 0.13–0.33 at
    /// similarities of only 0.14–0.35). What lies between is mostly the real
    /// faces of passers-by, and those simply stay unassigned.</para>
    ///
    /// <para>The value can hardly be too generous: the matching afterwards
    /// checks independently against <see cref="DefaultMatchThreshold"/> and the
    /// margin to the second best. An uncertain find becomes "not assigned" and
    /// gets no wrong name — it costs one entry in the assignment
    /// dialog.</para></param>
    public FaceService(FaceModelProvider models, float scoreThreshold = 0.40f)
    {
        _detector = CvDnn.ReadNetFromOnnx(models.DetectorPath)
                    ?? throw new InvalidOperationException("The face detection model could not be loaded.");
        _recognizer = CvDnn.ReadNetFromOnnx(models.RecognizerPath)
                      ?? throw new InvalidOperationException("The face feature model could not be loaded.");
        _scoreThreshold = scoreThreshold;
    }

    public List<DetectedFace> DetectFaces(string imagePath, bool includeCrops = false)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
            return image.Empty() ? [] : DetectCore(image, includeCrops);
        }
    }

    /// <summary>For image data already in memory, a single video frame for
    /// instance.</summary>
    public List<DetectedFace> DetectFaces(byte[] imageBytes, bool includeCrops = false)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var image = Cv2.ImDecode(imageBytes, ImreadModes.Color);
            return image.Empty() ? [] : DetectCore(image, includeCrops);
        }
    }

    private List<DetectedFace> DetectCore(Mat image, bool includeCrops)
    {
        // Scale uniformly and pad to multiples of 32. Scaling the axes
        // differently would distort faces and corrupt the vectors.
        var scale = Math.Min(1.0, DetectionSize / (double)Math.Max(image.Width, image.Height));
        var scaledWidth = Math.Max(32, (int)Math.Round(image.Width * scale));
        var scaledHeight = Math.Max(32, (int)Math.Round(image.Height * scale));
        var paddedWidth = (scaledWidth + 31) / 32 * 32;
        var paddedHeight = (scaledHeight + 31) / 32 * 32;

        using var resized = new Mat();
        Cv2.Resize(image, resized, new Size(scaledWidth, scaledHeight),
            interpolation: InterpolationFlags.Area);
        using var padded = new Mat(paddedHeight, paddedWidth, image.Type(), Scalar.All(0));
        resized.CopyTo(new Mat(padded, new Rect(0, 0, scaledWidth, scaledHeight)));

        using var blob = CvDnn.BlobFromImage(padded, 1.0, new Size(paddedWidth, paddedHeight));
        _detector.SetInput(blob);

        var names = new List<string>();
        foreach (var stride in Strides)
        {
            names.Add($"cls_{stride}");
            names.Add($"obj_{stride}");
            names.Add($"bbox_{stride}");
            names.Add($"kps_{stride}");
        }

        var outputs = names.Select(_ => new Mat()).ToArray();
        try
        {
            _detector.Forward(outputs, names);
            return Postprocess(image, outputs, paddedWidth, paddedHeight, scale, includeCrops);
        }
        finally
        {
            foreach (var output in outputs)
            {
                output.Dispose();
            }
        }
    }

    /// <summary>Decodes the YuNet outputs (three resolution levels) into
    /// boxes, landmarks and scores, and filters overlaps by non-maximum
    /// suppression.</summary>
    private List<DetectedFace> Postprocess(
        Mat original, Mat[] outputs, int paddedWidth, int paddedHeight, double scale, bool includeCrops)
    {
        var boxes = new List<Rect>();
        var scores = new List<float>();
        var landmarkSets = new List<Point2f[]>();

        for (var s = 0; s < Strides.Length; s++)
        {
            var stride = Strides[s];
            var cls = outputs[s * 4];
            var obj = outputs[s * 4 + 1];
            var bbox = outputs[s * 4 + 2];
            var kps = outputs[s * 4 + 3];

            var cols = paddedWidth / stride;
            var priorCount = cls.Size(1);

            for (var i = 0; i < priorCount; i++)
            {
                // Score: the geometric mean of the class and object
                // probabilities — the way OpenCV does it too.
                var score = (float)Math.Sqrt(
                    Math.Clamp(cls.At<float>(0, i, 0), 0f, 1f) *
                    Math.Clamp(obj.At<float>(0, i, 0), 0f, 1f));
                if (score < _scoreThreshold)
                {
                    continue;
                }

                var col = i % cols;
                var row = i / cols;

                var cx = (col + bbox.At<float>(0, i, 0)) * stride;
                var cy = (row + bbox.At<float>(0, i, 1)) * stride;
                var w = (float)Math.Exp(bbox.At<float>(0, i, 2)) * stride;
                var h = (float)Math.Exp(bbox.At<float>(0, i, 3)) * stride;

                // Back into the original coordinates, undoing the scaling.
                var x1 = (cx - w / 2) / scale;
                var y1 = (cy - h / 2) / scale;
                var boxWidth = w / scale;
                var boxHeight = h / scale;

                if (boxWidth < 24 || boxHeight < 24)
                {
                    // Winzige Gesichter liefern unbrauchbare Vektoren.
                    continue;
                }

                var landmarks = new Point2f[5];
                for (var k = 0; k < 5; k++)
                {
                    landmarks[k] = new Point2f(
                        (float)(((col + kps.At<float>(0, i, k * 2)) * stride) / scale),
                        (float)(((row + kps.At<float>(0, i, k * 2 + 1)) * stride) / scale));
                }

                boxes.Add(new Rect((int)x1, (int)y1, (int)boxWidth, (int)boxHeight));
                scores.Add(score);
                landmarkSets.Add(landmarks);
            }
        }

        if (boxes.Count == 0)
        {
            return [];
        }

        CvDnn.NMSBoxes(boxes, scores, _scoreThreshold, 0.3f, out var keep);

        var results = new List<DetectedFace>(keep.Length);
        foreach (var index in keep)
        {
            var embedding = ComputeEmbedding(original, landmarkSets[index], includeCrops, out var crop);
            if (embedding is null)
            {
                continue;
            }

            var box = boxes[index];
            results.Add(new DetectedFace
            {
                Box = box,
                Score = scores[index],
                Embedding = embedding,
                CropJpeg = crop,
                RelativeCenterX = Math.Clamp((box.X + box.Width / 2.0) / original.Width, 0, 1),
                RelativeHeight = Math.Clamp(box.Height / (double)original.Height, 0, 1),
            });
        }

        return results;
    }

    /// <summary>Aligns the face by its landmarks and computes the feature
    /// vector.</summary>
    private float[]? ComputeEmbedding(Mat image, Point2f[] landmarks, bool includeCrop, out byte[]? crop)
    {
        crop = null;
        try
        {
            using var transform = Cv2.EstimateAffinePartial2D(
                InputArray.Create(landmarks), InputArray.Create(AlignmentTemplate));
            // Degenerate landmarks — all five points almost on top of each
            // other — yield no matrix at all, not merely an empty one.
            if (transform is null || transform.Empty())
            {
                return null;
            }

            using var aligned = new Mat();
            Cv2.WarpAffine(image, aligned, transform, new Size(112, 112),
                flags: InterpolationFlags.Linear);

            if (includeCrop)
            {
                Cv2.ImEncode(".jpg", aligned, out crop, [(int)ImwriteFlags.JpegQuality, 90]);
            }

            using var blob = CvDnn.BlobFromImage(aligned, 1.0, new Size(112, 112));
            _recognizer.SetInput(blob);
            using var feature = _recognizer.Forward();
            if (feature is null || feature.Empty())
            {
                return null;
            }

            var embedding = new float[feature.Cols];
            for (var c = 0; c < feature.Cols; c++)
            {
                embedding[c] = feature.At<float>(0, c);
            }

            return NormalizeVector(embedding);
        }
        catch (Exception ex)
        {
            RunLog.Log($"Gesichtsvektor fehlgeschlagen: {ex.Message}");
            return null;
        }
    }

    /// <summary>L2 normalisation — after this a cosine comparison is a plain
    /// dot product.</summary>
    private static float[] NormalizeVector(float[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        return norm < 1e-9 ? vector : vector.Select(v => (float)(v / norm)).ToArray();
    }

    public static double Similarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return -1;
        }

        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>Matches a face to the most similar known person. Null when
    /// nobody reaches the threshold OR the two best candidates sit too close
    /// together, which means the match is ambiguous.</summary>
    public static PersonMatch? Match(
        float[] embedding,
        IReadOnlyList<KnownPerson> persons,
        double threshold = DefaultMatchThreshold)
    {
        var ranked = persons
            .Where(p => p.Embeddings.Count > 0)
            .Select(p => (Person: p, Score: p.Embeddings.Max(e => Similarity(embedding, e))))
            .OrderByDescending(s => s.Score)
            .ToList();

        if (ranked.Count == 0 || ranked[0].Score < threshold)
        {
            return null;
        }

        if (ranked.Count > 1 && ranked[0].Score - ranked[1].Score < AmbiguityMargin)
        {
            RunLog.Log($"Face ambiguous: {ranked[0].Person.Name} ({ranked[0].Score:F3}) " +
                       $"vs. {ranked[1].Person.Name} ({ranked[1].Score:F3}) — not assigned.");
            return null;
        }

        return new PersonMatch(ranked[0].Person, ranked[0].Score);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _detector.Dispose();
            _recognizer.Dispose();
        }
    }
}
