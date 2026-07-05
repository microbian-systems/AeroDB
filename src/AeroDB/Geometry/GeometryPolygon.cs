using System.Globalization;

namespace AeroDB;

/// <summary>
/// Represents a geographic polygon. Each ring is a list of (longitude, latitude) points.
/// The first ring is the exterior boundary; subsequent rings are interior holes.
/// Serializes to SurrealDB GeoJSON format.
/// </summary>
public class GeometryPolygon
{
    public List<List<(double Lng, double Lat)>> Rings { get; set; } = [];

    public GeometryPolygon() { }

    public GeometryPolygon(List<(double Lng, double Lat)> exteriorRing)
    {
        Rings = [exteriorRing];
    }

    public GeometryPolygon(List<(double, double)> exteriorRing, params List<(double, double)>[] holes)
    {
        Rings = [exteriorRing, .. holes];
    }

    /// <summary>
    /// Returns the SurrealQL-compatible GeoJSON literal.
    /// </summary>
    public string ToSurrealQL()
    {
        var coords = string.Join(", ", Rings.Select(ring =>
            "[" + string.Join(", ", ring.Select(p => $"[{p.Lng.ToInvariantString()}, {p.Lat.ToInvariantString()}]")) + "]"));
        return $"{{ type: 'Polygon', coordinates: [{coords}] }}";
    }

    public override string ToString() => ToSurrealQL();
}
