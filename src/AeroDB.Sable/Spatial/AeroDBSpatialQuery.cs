using System.Linq.Expressions;
using System.Text;
using System.Globalization;
using AeroDB.Sable.Internals.Cbor;
using AeroDB.Sable.Metadata;

namespace AeroDB.Sable;

public sealed class AeroDBSpatialQuery<T> : ISpatialQuery<T> where T : class
{
    private const double HalfEarthCircumferenceMeters = 20_015_086.796020572;
    private readonly SurrealQueryProvider _provider;
    private readonly string _table;
    private readonly string _legacyTable;
    private string? _legacyNearByField;
    private string? _legacyWithinField;
    private string? _legacyOrderByDistanceField;
    private string? _nearByField;
    private double _nearByLat;
    private double _nearByLng;
    private double _nearByMaxDistance;
    private string? _withinField;
    private List<(double Lng, double Lat)>? _withinPolygon;
    private List<(double Lng, double Lat)>? _legacyWithinPolygon;
    private string? _orderByDistanceField;
    private double _orderByLat;
    private double _orderByLng;
    private string? _whereClause;
    private string? _legacyWhereClause;
    private Expression<Func<T, bool>>? _strictWherePredicate;
    private readonly SurrealCommandBuilder _legacyParameterBuilder = new();
    private int _limit = 100;
    private int _skip;

    internal AeroDBSpatialQuery(SurrealQueryProvider provider)
    {
        _provider = provider;
        if (EncryptedFieldResolver.HasEncryptedFields(typeof(T), provider.StoreOptions.Schema))
            throw new SableEncryptedOperationNotSupportedException(typeof(T), "spatial query");
        _legacyTable = MetadataDispatch.GetTableName(typeof(T));
        _table = MetadataDispatch.GetTableName(typeof(T), provider.StoreOptions.Schema);
    }

    public ISpatialQuery<T> NearBy(Expression<Func<T, object>> locationField, double latitude, double longitude, double maxDistanceMeters)
    {
        _withinField = null;
        _withinPolygon = null;
        _orderByDistanceField = null;
        _legacyNearByField = GetMemberName(locationField);
        _nearByField = GetStorageFieldName(locationField);
        _nearByLat = latitude;
        _nearByLng = longitude;
        _nearByMaxDistance = maxDistanceMeters;
        return this;
    }

    public ISpatialQuery<T> Within(Expression<Func<T, object>> locationField, List<(double Lng, double Lat)> polygon)
    {
        _nearByField = null;
        _orderByDistanceField = null;
        _legacyWithinField = GetMemberName(locationField);
        _withinField = GetStorageFieldName(locationField);
        _legacyWithinPolygon = polygon;
        _withinPolygon = polygon is null ? null : [.. polygon];
        return this;
    }

    public ISpatialQuery<T> OrderByDistance(Expression<Func<T, object>> locationField, double latitude, double longitude)
    {
        _nearByField = null;
        _withinField = null;
        _withinPolygon = null;
        _legacyOrderByDistanceField = GetMemberName(locationField);
        _orderByDistanceField = GetStorageFieldName(locationField);
        _orderByLat = latitude;
        _orderByLng = longitude;
        return this;
    }

    public ISpatialQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        _legacyWhereClause = SurrealExpressionVisitor.TranslateCondition(predicate.Body, _legacyParameterBuilder);
        _strictWherePredicate = predicate;
        return this;
    }

    public ISpatialQuery<T> Take(int limit)
    {
        _limit = limit;
        return this;
    }

    public ISpatialQuery<T> Skip(int count)
    {
        _skip = count;
        return this;
    }

    public async Task<List<T>> ToListAsync(CancellationToken ct = default)
    {
        var surql = BuildLegacySurql();
        var response = await _provider.Session.RawQuery(surql, _legacyParameterBuilder.Parameters, ct).ConfigureAwait(false);
        if (response.HasErrors || response.Count == 0)
            return [];

        if (_provider.SessionBase is not null)
        {
            var mapped = _provider.SessionBase.DeserializeMappedPocoResponse<T>(response);
            if (mapped.Count > 0)
                return mapped;
        }

        return response.GetValue<List<T>>(0) ?? [];
    }

    internal async Task<List<SpatialDistanceResult<T>>> ExecuteWithDistanceAsync(CancellationToken ct)
    {
        if (_withinField is not null)
            throw new InvalidOperationException("Distance materialization is only supported for NearBy and OrderByDistance queries.");

        var (response, includesDistance) = await ExecuteResponseAsync(ct).ConfigureAwait(false);
        if (response.HasErrors)
            throw new InvalidOperationException("Spatial distance query returned a provider error.");
        var status = CborResultReader.ReadPocoResultStrict(response, 0, out var records);
        if (status == CborResultReader.StrictPocoResultStatus.EmptyRows)
            return [];
        if (status != CborResultReader.StrictPocoResultStatus.Rows)
            throw new InvalidDataException("Spatial distance query returned an unreadable or empty provider response.");

        var distances = ValidateDistanceRows(records, includesDistance);

        var documents = DeserializeDocuments(records);
        if (documents.Count != records.Count)
            throw new InvalidDataException("Spatial distance query response could not be materialized one document at a time.");

        var results = new List<SpatialDistanceResult<T>>(documents.Count);
        for (var index = 0; index < documents.Count; index++)
        {
            results.Add(new SpatialDistanceResult<T>(documents[index], distances[index]));
        }
        return results;
    }

    private async Task<(SurrealDb.Net.Models.Response.SurrealDbResponse Response, bool IncludesDistance)> ExecuteResponseAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var builder = new SurrealCommandBuilder();
        var (surql, includesDistance) = BuildStrictSurql(builder);
        var response = await _provider.ExecuteSpatialAsync<T>(surql, builder.Parameters, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return (response, includesDistance);
    }

    private List<T> DeserializeDocuments(List<Dictionary<string, object?>> records)
    {
        var mapping = _provider.StoreOptions.Schema.Mappings.GetValueOrDefault(typeof(T));
        return InternalSessionBase.DeserializePocoFromList<T>(
            records,
            mapping?.IdentityProperty ?? "Id",
            _provider.StoreOptions.Schema,
            _provider.StoreOptions.EnumStorage);
    }

    // ISpatialQuery<T>.ToListAsync is a legacy surface. Its CLR member names,
    // literal geometry/paging values, and ordering are compatibility contracts.
    // The strict mapped/parameterized path is intentionally limited to the
    // additive distance-materialization API below.
    private string BuildLegacySurql()
    {
        if (_legacyNearByField is not null)
        {
            var bbox = SpatialBbox.Compute(_nearByLat, _nearByLng, _nearByMaxDistance);
            var point = $"({_nearByLng.ToString(CultureInfo.InvariantCulture)}, {_nearByLat.ToString(CultureInfo.InvariantCulture)})";
            var polygon = $"{{ type: 'Polygon', coordinates: [[[{bbox.MinLng.ToString(CultureInfo.InvariantCulture)},{bbox.MinLat.ToString(CultureInfo.InvariantCulture)}],[{bbox.MaxLng.ToString(CultureInfo.InvariantCulture)},{bbox.MinLat.ToString(CultureInfo.InvariantCulture)}],[{bbox.MaxLng.ToString(CultureInfo.InvariantCulture)},{bbox.MaxLat.ToString(CultureInfo.InvariantCulture)}],[{bbox.MinLng.ToString(CultureInfo.InvariantCulture)},{bbox.MaxLat.ToString(CultureInfo.InvariantCulture)}],[{bbox.MinLng.ToString(CultureInfo.InvariantCulture)},{bbox.MinLat.ToString(CultureInfo.InvariantCulture)}]]] }}";
            var parts = new List<string>
            {
                $"{_legacyNearByField} INSIDE {polygon}",
                $"geo::DISTANCE({_legacyNearByField}, {point}) <= {_nearByMaxDistance.ToString(CultureInfo.InvariantCulture)}"
            };
            if (_legacyWhereClause is not null) parts.Add($"({_legacyWhereClause})");
            return $"SELECT *, geo::DISTANCE({_legacyNearByField}, {point}) AS _distance FROM `{_legacyTable}` WHERE {string.Join(" AND ", parts)} ORDER BY _distance ASC LIMIT {_limit}" + (_skip > 0 ? $" START AT {_skip}" : "") + ";";
        }

        if (_legacyWithinField is not null)
        {
            var coordinates = string.Join(", ", _legacyWithinPolygon!.Select(point => $"[{point.Lng.ToString(CultureInfo.InvariantCulture)}, {point.Lat.ToString(CultureInfo.InvariantCulture)}]"));
            var sql = $"SELECT * FROM `{_legacyTable}` WHERE {_legacyWithinField} INSIDE {{ type: 'Polygon', coordinates: [[{coordinates}]] }}";
            if (_legacyWhereClause is not null) sql += $" AND ({_legacyWhereClause})";
            return sql + $" LIMIT {_limit}" + (_skip > 0 ? $" START AT {_skip}" : "") + ";";
        }

        if (_legacyOrderByDistanceField is not null)
        {
            var point = $"({_orderByLng.ToString(CultureInfo.InvariantCulture)}, {_orderByLat.ToString(CultureInfo.InvariantCulture)})";
            var sql = $"SELECT *, geo::DISTANCE({_legacyOrderByDistanceField}, {point}) AS _distance FROM `{_legacyTable}`";
            if (_legacyWhereClause is not null) sql += $" WHERE ({_legacyWhereClause})";
            return sql + $" ORDER BY _distance ASC LIMIT {_limit}" + (_skip > 0 ? $" START AT {_skip}" : "") + ";";
        }

        throw new InvalidOperationException("Spatial query requires .NearBy(), .Within(), or .OrderByDistance() before executing.");
    }

    private (string Surql, bool IncludesDistance) BuildStrictSurql(SurrealCommandBuilder builder)
    {
        if (EncryptedFieldResolver.HasEncryptedFields(typeof(T), _provider.StoreOptions.Schema))
            throw new SableEncryptedOperationNotSupportedException(typeof(T), "spatial query");

        ValidatePaging();
        var whereParts = new List<string>();
        if (_strictWherePredicate is not null)
        {
            _whereClause = SurrealExpressionVisitor.TranslateCondition(
                _strictWherePredicate.Body,
                builder,
                _provider.StoreOptions.Schema,
                _provider.StoreOptions.EnumStorage);
            whereParts.Add($"({_whereClause})");
        }
        _provider.ApplySpatialPolicyFilters<T>(whereParts, builder);

        if (_nearByField is not null)
        {
            ValidatePoint(_nearByLat, _nearByLng);
            ValidateRadius(_nearByMaxDistance);
            return (BuildNearBySurql(builder, whereParts), true);
        }
        if (_withinField is not null)
        {
            ValidatePolygon(_withinPolygon);
            return (BuildWithinSurql(builder, whereParts), false);
        }
        if (_orderByDistanceField is not null)
        {
            ValidatePoint(_orderByLat, _orderByLng);
            return (BuildOrderByDistanceSurql(builder, whereParts), true);
        }
        throw new InvalidOperationException("Spatial query requires .NearBy(), .Within(), or .OrderByDistance() before executing.");
    }

    private string BuildNearBySurql(SurrealCommandBuilder builder, List<string> whereParts)
    {
        var point = new GeometryPoint(_nearByLng, _nearByLat);
        var pointParameter = builder.Parameter(point);
        if (_nearByMaxDistance > 0d)
        {
            var bbox = SpatialBbox.ComputeSpherical(_nearByLat, _nearByLng, _nearByMaxDistance);
            foreach (var prefilter in bbox.CreatePrefilters(_nearByField!, builder))
                whereParts.Add(prefilter);
        }
        whereParts.Add($"geo::DISTANCE({_nearByField}, {pointParameter}) <= {builder.Parameter(_nearByMaxDistance)}");
        return BuildSelect(whereParts, $"geo::DISTANCE({_nearByField}, {pointParameter}) AS _distance", "_distance ASC, id ASC", builder);
    }

    private string BuildWithinSurql(SurrealCommandBuilder builder, List<string> whereParts)
    {
        whereParts.Add($"{_withinField} INSIDE {builder.Parameter(new GeometryPolygon(_withinPolygon!))}");
        return BuildSelect(whereParts, null, "id ASC", builder);
    }

    private string BuildOrderByDistanceSurql(SurrealCommandBuilder builder, List<string> whereParts)
    {
        var pointParameter = builder.Parameter(new GeometryPoint(_orderByLng, _orderByLat));
        return BuildSelect(whereParts, $"geo::DISTANCE({_orderByDistanceField}, {pointParameter}) AS _distance", "_distance ASC, id ASC", builder);
    }

    private string BuildSelect(List<string> whereParts, string? distanceProjection, string orderBy, SurrealCommandBuilder builder)
    {
        var projection = distanceProjection is null ? "*" : $"*, {distanceProjection}";
        var sql = new StringBuilder($"SELECT {projection} FROM `{_table}`");
        if (whereParts.Count > 0)
            sql.Append(" WHERE ").Append(string.Join(" AND ", whereParts));
        sql.Append(" ORDER BY ").Append(orderBy);
        sql.Append(" LIMIT ").Append(builder.Parameter(_limit));
        if (_skip > 0)
            sql.Append(" START AT ").Append(builder.Parameter(_skip));
        return sql.Append(';').ToString();
    }

    private string GetStorageFieldName(Expression<Func<T, object>> selector)
        => MetadataDispatch.GetFieldName(typeof(T), GetMemberName(selector), _provider.StoreOptions.Schema);

    internal static double[] ValidateDistanceRows(
        IReadOnlyList<Dictionary<string, object?>> records,
        bool includesDistance)
    {
        var distances = new double[records.Count];
        for (var index = 0; index < records.Count; index++)
        {
            if (!includesDistance
                || !records[index].TryGetValue("_distance", out var rawDistance)
                || !TryGetFiniteNonNegativeDistance(rawDistance, out var distance))
            {
                throw new InvalidDataException("Spatial distance query returned a document without one finite, non-negative numeric _distance value.");
            }

            distances[index] = distance;
        }

        return distances;
    }

    private static bool TryGetFiniteNonNegativeDistance(object? value, out double distance)
    {
        distance = value switch
        {
            byte number => number,
            sbyte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            float number => number,
            double number => number,
            decimal number => (double)number,
            _ => double.NaN
        };
        return double.IsFinite(distance) && distance >= 0d;
    }

    private void ValidatePaging()
    {
        if (_limit is <= 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(_limit), "Spatial query limits must be between 1 and 1,000.");
        if (_skip < 0)
            throw new ArgumentOutOfRangeException(nameof(_skip));

        try { _ = checked(_skip + _limit); }
        catch (OverflowException) { throw new ArgumentOutOfRangeException(nameof(_skip), "Skip and limit exceed the supported range."); }
    }

    private static void ValidatePoint(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || latitude is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(latitude));
        if (!double.IsFinite(longitude) || longitude is < -180 or > 180)
            throw new ArgumentOutOfRangeException(nameof(longitude));
    }

    private static void ValidateRadius(double radius)
    {
        if (!double.IsFinite(radius) || radius < 0 || radius > HalfEarthCircumferenceMeters)
            throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be finite and no greater than half the Earth's circumference.");
    }

    private static void ValidatePolygon(List<(double Lng, double Lat)>? polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count < 4 || polygon.Count > 10_000)
            throw new ArgumentOutOfRangeException(nameof(polygon), "A polygon must contain between 4 and 10,000 coordinates.");
        if (polygon[0] != polygon[^1])
            throw new ArgumentException("A polygon ring must be closed.", nameof(polygon));
        foreach (var point in polygon)
            ValidatePoint(point.Lat, point.Lng);
    }

    private static string GetMemberName(Expression<Func<T, object>> selector)
    {
        if (selector.Body is MemberExpression m)
            return m.Member.Name;
        if (selector.Body is UnaryExpression { NodeType: ExpressionType.Convert, Operand: MemberExpression um })
            return um.Member.Name;
        throw new ArgumentException("Selector must be a simple member expression");
    }
}
