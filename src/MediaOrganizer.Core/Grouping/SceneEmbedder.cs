using MediaOrganizer.Core.Faces;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace MediaOrganizer.Core.Grouping;

/// <summary>Turns a whole picture into one L2-normalised vector, so that two
/// shots can be compared by a dot product.
///
/// <para>This is what the language model cannot do: it only ever sees a
/// one-line description, and "Anna on a wooden structure by a tree" reads much
/// like "Anna with a wooden toy on a carpet". Measured on those two: the
/// descriptions put them in one folder, the vectors 0.59 apart.</para></summary>
public sealed class SceneEmbedder : IDisposable
{
    /// <summary>Input size the model expects.</summary>
    private static readonly Size InputSize = new(224, 224);

    /// <summary>ImageNet channel means, the normalisation this model was
    /// trained with.</summary>
    private static readonly Scalar Mean = new(0.485 * 255, 0.456 * 255, 0.406 * 255);

    private readonly Net _net;
    private readonly object _sync = new();

    public SceneEmbedder(FaceModelProvider models)
    {
        _net = CvDnn.ReadNetFromOnnx(models.SceneEncoderPath)
               ?? throw new InvalidOperationException(
                   $"Scene encoder could not be loaded: {models.SceneEncoderPath}");
    }

    /// <summary>The vector for a prepared JPEG — the path everything takes,
    /// because the preprocessor already handles HEIC and video frames through
    /// the Windows codecs. Null when the bytes cannot be decoded.</summary>
    public float[]? Embed(byte[] jpeg)
    {
        using var image = Cv2.ImDecode(jpeg, ImreadModes.Color);
        return image is null || image.Empty() ? null : Embed(image);
    }

    /// <summary>The vector for an already decoded image — the path the video
    /// frames and the preprocessed JPEGs take.</summary>
    public float[]? Embed(Mat image)
    {
        if (image.Empty())
        {
            return null;
        }

        using var blob = CvDnn.BlobFromImage(
            image, 1.0 / 255, InputSize, Mean, swapRB: true, crop: false);

        lock (_sync)
        {
            _net.SetInput(blob);
            using var output = _net.Forward();
            output.GetArray(out float[] values);
            return Normalise(values);
        }
    }

    /// <summary>Cosine similarity of two normalised vectors: 1 identical, 0
    /// unrelated. Measured on real shots: near-duplicates 0.97–0.99, the same
    /// occasion from another angle about 0.67, different occasions 0.09–0.61.</summary>
    public static float Similarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            return 0;
        }

        var sum = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>One vector out of several — for a video, whose single frame is
    /// a poor representative. Null when nothing could be encoded.</summary>
    public static float[]? Average(IReadOnlyList<float[]?> vectors)
    {
        var usable = vectors.Where(v => v is { Length: > 0 }).ToList();
        if (usable.Count == 0)
        {
            return null;
        }

        var sum = new float[usable[0]!.Length];
        foreach (var vector in usable)
        {
            for (var i = 0; i < sum.Length && i < vector!.Length; i++)
            {
                sum[i] += vector[i];
            }
        }

        return Normalise(sum);
    }

    private static float[] Normalise(float[] values)
    {
        var length = 0f;
        foreach (var value in values)
        {
            length += value * value;
        }

        length = MathF.Sqrt(length);
        if (length <= 0)
        {
            return values;
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= length;
        }

        return values;
    }

    public void Dispose() => _net.Dispose();
}
