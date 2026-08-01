namespace AeroDB.Sable;

/// <summary>Bounding-box helpers for legacy and recall-safe spatial queries.</summary>
internal static class SpatialBbox
{
    private const double MetersPerDegreeLat = 111_320d;
    private const double EarthRadiusMeters = 6_371_008.8;

    /// <summary>Preserves the original approximate bounding-box contract used by <c>ToListAsync</c>.</summary>
    public static Bbox Compute(double centerLat, double centerLng, double maxDistanceMeters)
    {
        var latDegrees = maxDistanceMeters / MetersPerDegreeLat;
        var lngDegrees = maxDistanceMeters / (MetersPerDegreeLat * Math.Cos(centerLat * Math.PI / 180d));

        // Preserve the original 5% approximation margin.
        latDegrees *= 1.05d;
        lngDegrees *= 1.05d;

        return new Bbox(
            centerLat - latDegrees,
            centerLat + latDegrees,
            centerLng - lngDegrees,
            centerLng + lngDegrees,
            coversAllLongitudes: false,
            crossesAntimeridian: false);
    }

    /// <summary>Computes the recall-safe spherical prefilter used by the additive distance API.</summary>
    public static Bbox ComputeSpherical(double centerLat, double centerLng, double maxDistanceMeters)
    {
        var angularDistance = maxDistanceMeters / EarthRadiusMeters;
        var latitudeDelta = angularDistance * 180d / Math.PI;
        var minLat = Math.Max(-90d, centerLat - latitudeDelta);
        var maxLat = Math.Min(90d, centerLat + latitudeDelta);
        var crossesPole = minLat <= -90d || maxLat >= 90d;
        var longitudeDelta = crossesPole
            ? 180d
            : Math.Asin(Math.Min(1d, Math.Sin(angularDistance) / Math.Max(1e-15, Math.Cos(centerLat * Math.PI / 180d)))) * 180d / Math.PI;

        if (longitudeDelta >= 180d)
            return new Bbox(minLat, maxLat, -180d, 180d, coversAllLongitudes: true, crossesAntimeridian: false);

        var minLng = NormalizeLongitude(centerLng - longitudeDelta);
        var maxLng = NormalizeLongitude(centerLng + longitudeDelta);
        return new Bbox(minLat, maxLat, minLng, maxLng, coversAllLongitudes: false, crossesAntimeridian: minLng > maxLng);
    }

    private static double NormalizeLongitude(double longitude)
    {
        var normalized = (longitude + 180d) % 360d;
        return normalized < 0d ? normalized + 180d : normalized - 180d;
    }

    internal readonly struct Bbox(
        double minLat,
        double maxLat,
        double minLng,
        double maxLng,
        bool coversAllLongitudes,
        bool crossesAntimeridian)
    {
        public double MinLat { get; } = minLat;
        public double MaxLat { get; } = maxLat;
        public double MinLng { get; } = minLng;
        public double MaxLng { get; } = maxLng;
        public bool CoversAllLongitudes { get; } = coversAllLongitudes;
        public bool CrossesAntimeridian { get; } = crossesAntimeridian;

        public IEnumerable<string> CreatePrefilters(string field, SurrealCommandBuilder parameters)
        {
            if (CoversAllLongitudes)
                return [];

            if (!CrossesAntimeridian)
                return [$"{field} INSIDE {parameters.Parameter(CreatePolygon(MinLng, MaxLng))}"];

            var west = parameters.Parameter(CreatePolygon(MinLng, 180d));
            var east = parameters.Parameter(CreatePolygon(-180d, MaxLng));
            return [$"({field} INSIDE {west} OR {field} INSIDE {east})"];
        }

        private GeometryPolygon CreatePolygon(double minLng, double maxLng)
            => new([
                (minLng, MinLat),
                (maxLng, MinLat),
                (maxLng, MaxLat),
                (minLng, MaxLat),
                (minLng, MinLat)
            ]);
    }
}
