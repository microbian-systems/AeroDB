namespace AeroDB.Sable;

/// <summary>Document materialized from a distance-bearing spatial query.</summary>
public sealed record SpatialDistanceResult<T>(T Document, double DistanceMeters) where T : class;
