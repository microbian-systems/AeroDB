using System.Globalization;

namespace AeroDB;

/// <summary>
/// Computes a geo bounding box from a center point and radius.
/// Approximate conversion: 1° lat ≈ 111.32 km, 1° lon ≈ 111.32 km × cos(lat).
/// </summary>
internal static class SpatialBbox
{
    private const double MetersPerDegreeLat = 111_320.0;

    /// (lat, lng, maxDistanceMeters) → Bbox boundaries
    public static Bbox Compute(double centerLat, double centerLng, double maxDistanceMeters)
    {
        double latDegrees = maxDistanceMeters / MetersPerDegreeLat;
        double lngDegrees = maxDistanceMeters / (MetersPerDegreeLat * Math.Cos(centerLat * Math.PI / 180.0));

        // Add 5% margin to account for sphere approximation
        latDegrees *= 1.05;
        lngDegrees *= 1.05;

        return new Bbox(
            centerLat - latDegrees,
            centerLat + latDegrees,
            centerLng - lngDegrees,
            centerLng + lngDegrees
        );
    }

    public readonly struct Bbox
    {
        public double MinLat { get; }
        public double MaxLat { get; }
        public double MinLng { get; }
        public double MaxLng { get; }

        public Bbox(double minLat, double maxLat, double minLng, double maxLng)
        {
            MinLat = minLat;
            MaxLat = maxLat;
            MinLng = minLng;
            MaxLng = maxLng;
        }
    }
}
