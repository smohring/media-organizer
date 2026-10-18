using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using MediaOrganizer.Core.Models;

namespace MediaOrganizer.Core.Geo;

/// <summary>Ergebnis einer Offline-Ortsauflösung.</summary>
public sealed record GeocodeResult(string Name, string CountryCode, double DistanceKm)
{
    /// <summary>Anzeigename; außerhalb Deutschlands mit Ländercode.</summary>
    public string DisplayName =>
        CountryCode is "DE" or "" ? Name : $"{Name} ({CountryCode})";
}

/// <summary>Offline-Reverse-Geocoding über eine eingebettete
/// GeoNames-Ortsdatenbank (cities1000, ~170k Orte weltweit, CC-BY 4.0) —
/// keinerlei Netzwerkzugriff, passend zur Lokalitäts-Vorgabe der App.
///
/// Nachschlagen über ein 1°-Gitter; unter den nahen Kandidaten wird der
/// einwohnerstärkste bevorzugt, damit Gruppen nach dem eigentlichen Ort
/// benannt werden und nicht nach dem zufällig nächstgelegenen Weiler.</summary>
public sealed class OfflineGeocoder
{
    private static readonly Lazy<OfflineGeocoder> LazyInstance = new(LoadEmbedded);

    public static OfflineGeocoder Instance => LazyInstance.Value;

    private readonly string[] _names;
    private readonly float[] _lats;
    private readonly float[] _lons;
    private readonly string[] _countries;
    private readonly int[] _populations;
    private readonly Dictionary<int, List<int>> _grid;

    private OfflineGeocoder(
        string[] names, float[] lats, float[] lons, string[] countries, int[] populations)
    {
        _names = names;
        _lats = lats;
        _lons = lons;
        _countries = countries;
        _populations = populations;

        _grid = [];
        for (var i = 0; i < names.Length; i++)
        {
            var key = CellKey(lats[i], lons[i]);
            if (!_grid.TryGetValue(key, out var bucket))
            {
                _grid[key] = bucket = [];
            }

            bucket.Add(i);
        }
    }

    private static OfflineGeocoder LoadEmbedded()
    {
        using var raw = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("MediaOrganizer.Core.cities.tsv.gz")
            ?? throw new InvalidOperationException("Eingebettete Ortsdatenbank fehlt.");
        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        var names = new List<string>(180_000);
        var lats = new List<float>(180_000);
        var lons = new List<float>(180_000);
        var countries = new List<string>(180_000);
        var populations = new List<int>(180_000);

        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split('\t');
            if (fields.Length < 5
                || !float.TryParse(fields[1], CultureInfo.InvariantCulture, out var lat)
                || !float.TryParse(fields[2], CultureInfo.InvariantCulture, out var lon))
            {
                continue;
            }

            names.Add(fields[0]);
            lats.Add(lat);
            lons.Add(lon);
            countries.Add(fields[3]);
            populations.Add(int.TryParse(fields[4], out var pop) ? pop : 0);
        }

        return new OfflineGeocoder([.. names], [.. lats], [.. lons], [.. countries], [.. populations]);
    }

    private static int CellKey(double lat, double lon) =>
        ((int)Math.Floor(lat) + 90) * 1000 + (int)Math.Floor(lon) + 180;

    /// <summary>Nächstgelegener Ort zur Koordinate oder null, wenn im Umkreis
    /// von <paramref name="maxDistanceKm"/> nichts liegt.</summary>
    public GeocodeResult? FindNearest(GeoPoint point, double maxDistanceKm = 50)
    {
        var candidates = new List<int>();

        // Ringe um die Zelle erweitern, bis Kandidaten gefunden sind; danach
        // einen Ring zusätzlich mitnehmen (Zellgrenzen-Effekte).
        var foundAtRing = -1;
        for (var ring = 0; ring <= 3; ring++)
        {
            CollectRing(point, ring, candidates);
            if (candidates.Count > 0 && foundAtRing < 0)
            {
                foundAtRing = ring;
            }

            if (foundAtRing >= 0 && ring >= foundAtRing + 1)
            {
                break;
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var bestIndex = -1;
        var bestDistance = double.MaxValue;
        foreach (var i in candidates)
        {
            var distance = point.DistanceKm(new GeoPoint(_lats[i], _lons[i]));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        if (bestIndex < 0 || bestDistance > maxDistanceKm)
        {
            return null;
        }

        // Unter den fast gleich nahen Kandidaten den größten Ort wählen.
        var tolerance = Math.Max(bestDistance * 1.4, bestDistance + 3.0);
        var chosen = bestIndex;
        foreach (var i in candidates)
        {
            if (_populations[i] <= _populations[chosen])
            {
                continue;
            }

            var distance = point.DistanceKm(new GeoPoint(_lats[i], _lons[i]));
            if (distance <= tolerance && distance <= maxDistanceKm)
            {
                chosen = i;
            }
        }

        return new GeocodeResult(
            _names[chosen],
            _countries[chosen],
            point.DistanceKm(new GeoPoint(_lats[chosen], _lons[chosen])));
    }

    private void CollectRing(GeoPoint point, int ring, List<int> candidates)
    {
        var latCell = (int)Math.Floor(point.Latitude);
        var lonCell = (int)Math.Floor(point.Longitude);

        for (var dLat = -ring; dLat <= ring; dLat++)
        {
            for (var dLon = -ring; dLon <= ring; dLon++)
            {
                if (Math.Max(Math.Abs(dLat), Math.Abs(dLon)) != ring)
                {
                    continue;
                }

                // Längengrad zyklisch behandeln (±180°-Übergang).
                var lon = lonCell + dLon;
                lon = lon < -180 ? lon + 360 : lon >= 180 ? lon - 360 : lon;
                var key = ((latCell + dLat) + 90) * 1000 + lon + 180;
                if (_grid.TryGetValue(key, out var bucket))
                {
                    candidates.AddRange(bucket);
                }
            }
        }
    }
}
