using System.Globalization;

namespace AeroDB.Sable;

/// <summary>
/// Represents a geographic point with longitude and latitude.
/// Serializes to SurrealDB's native geometry type as a (lon, lat) tuple.
/// </summary>
public class GeometryPoint
{
    public double Lng { get; set; }
    public double Lat { get; set; }

    public GeometryPoint() { }

    public GeometryPoint(double lng, double lat)
    {
        Lng = lng;
        Lat = lat;
    }

    public override string ToString() => $"({Lng}, {Lat})";

    /// <summary>
    /// Returns the SurrealQL-compatible literal: (lng, lat)
    /// </summary>
    public string ToSurrealQL() => $"({Lng.ToInvariantString()}, {Lat.ToInvariantString()})";

    public void Deconstruct(out double lng, out double lat)
    {
        lng = Lng;
        lat = Lat;
    }
}
