using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using AeroDB.Metadata;

namespace AeroDB;

public sealed class AeroDBSpatialQuery<T> : ISpatialQuery<T> where T : class
{
    private readonly SurrealQueryProvider _provider;
    private readonly string _table;

    // NearBy state
    private string? _nearByField;
    private double _nearByLat;
    private double _nearByLng;
    private double _nearByMaxDistance;

    // Within state
    private string? _withinField;
    private List<(double Lng, double Lat)>? _withinPolygon;

    // OrderByDistance state
    private string? _orderByDistanceField;
    private double _orderByLat;
    private double _orderByLng;

    // Filter / pagination
    private Expression<Func<T, bool>>? _wherePredicate;
    private string? _whereClause;
    private int _limit = 100;
    private int _skip;

    private readonly SurrealCommandBuilder _paramBuilder = new();

    internal AeroDBSpatialQuery(SurrealQueryProvider provider)
    {
        _provider = provider;
        _table = MetadataDispatch.GetTableName(typeof(T));
    }

    public ISpatialQuery<T> NearBy(Expression<Func<T, object>> locationField, double latitude, double longitude, double maxDistanceMeters)
    {
        _nearByField = GetMemberName(locationField);
        _nearByLat = latitude;
        _nearByLng = longitude;
        _nearByMaxDistance = maxDistanceMeters;
        return this;
    }

    public ISpatialQuery<T> Within(Expression<Func<T, object>> locationField, List<(double Lng, double Lat)> polygon)
    {
        _withinField = GetMemberName(locationField);
        _withinPolygon = polygon;
        return this;
    }

    public ISpatialQuery<T> OrderByDistance(Expression<Func<T, object>> locationField, double latitude, double longitude)
    {
        _orderByDistanceField = GetMemberName(locationField);
        _orderByLat = latitude;
        _orderByLng = longitude;
        return this;
    }

    public ISpatialQuery<T> Where(Expression<Func<T, bool>> predicate)
    {
        _wherePredicate = predicate;
        _whereClause = SurrealExpressionVisitor.TranslateCondition(predicate.Body, _paramBuilder);
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
        if (_nearByField is not null)
            return await ExecuteNearByAsync(ct).ConfigureAwait(false);
        if (_withinField is not null)
            return await ExecuteWithinAsync(ct).ConfigureAwait(false);
        if (_orderByDistanceField is not null)
            return await ExecuteOrderByDistanceAsync(ct).ConfigureAwait(false);
        throw new InvalidOperationException("Spatial query requires .NearBy(), .Within(), or .OrderByDistance() before executing.");
    }

    private async Task<List<T>> ExecuteNearByAsync(CancellationToken ct)
    {
        var field = _nearByField!;
        var lat = _nearByLat;
        var lng = _nearByLng;
        var maxDist = _nearByMaxDistance;

        // Compute bounding box
        var bbox = SpatialBbox.Compute(lat, lng, maxDist);

        var sb = new StringBuilder();
        sb.Append("SELECT *");

        // Add distance projection
        sb.Append($", geo::DISTANCE({field}, ({lng.ToInvariantString()}, {lat.ToInvariantString()})) AS _distance");

        sb.Append($" FROM `{_table}`");

        // Build WHERE clause with bbox pre-filter
        var whereParts = new List<string>();
        whereParts.Add($"{field} INSIDE {{ type: 'Polygon', coordinates: [[[{bbox.MinLng.ToInvariantString()},{bbox.MinLat.ToInvariantString()}],[{bbox.MaxLng.ToInvariantString()},{bbox.MinLat.ToInvariantString()}],[{bbox.MaxLng.ToInvariantString()},{bbox.MaxLat.ToInvariantString()}],[{bbox.MinLng.ToInvariantString()},{bbox.MaxLat.ToInvariantString()}],[{bbox.MinLng.ToInvariantString()},{bbox.MinLat.ToInvariantString()}]]] }}");
        whereParts.Add($"geo::DISTANCE({field}, ({lng.ToInvariantString()}, {lat.ToInvariantString()})) <= {maxDist.ToInvariantString()}");

        if (_whereClause is not null)
            whereParts.Add($"({_whereClause})");

        sb.Append(" WHERE ");
        sb.Append(string.Join(" AND ", whereParts));

        // Order by distance
        sb.Append(" ORDER BY _distance ASC");

        // Limit / Skip
        sb.Append($" LIMIT {_limit}");
        if (_skip > 0)
            sb.Append($" START AT {_skip}");

        sb.Append(';');
        return await ExecuteRawSpatialAsync(sb.ToString(), ct).ConfigureAwait(false);
    }

    private async Task<List<T>> ExecuteWithinAsync(CancellationToken ct)
    {
        var field = _withinField!;
        var polygon = _withinPolygon!;

        // Build polygon literal
        var coords = string.Join(", ", polygon.Select(p =>
            $"[{p.Lng.ToInvariantString()}, {p.Lat.ToInvariantString()}]"));
        var polygonLit = $"{{ type: 'Polygon', coordinates: [[{coords}]] }}";

        var sb = new StringBuilder();
        sb.Append($"SELECT * FROM `{_table}` WHERE {field} INSIDE {polygonLit}");

        if (_whereClause is not null)
            sb.Append($" AND ({_whereClause})");

        sb.Append($" LIMIT {_limit}");
        if (_skip > 0)
            sb.Append($" START AT {_skip}");

        sb.Append(';');
        return await ExecuteRawSpatialAsync(sb.ToString(), ct).ConfigureAwait(false);
    }

    private async Task<List<T>> ExecuteOrderByDistanceAsync(CancellationToken ct)
    {
        var field = _orderByDistanceField!;
        var lat = _orderByLat;
        var lng = _orderByLng;

        var sb = new StringBuilder();
        sb.Append("SELECT *");
        sb.Append($", geo::DISTANCE({field}, ({lng.ToInvariantString()}, {lat.ToInvariantString()})) AS _distance");
        sb.Append($" FROM `{_table}`");

        var whereParts = new List<string>();
        if (_whereClause is not null)
            whereParts.Add($"({_whereClause})");

        if (whereParts.Count > 0)
            sb.Append(" WHERE ").Append(string.Join(" AND ", whereParts));

        sb.Append(" ORDER BY _distance ASC");
        sb.Append($" LIMIT {_limit}");
        if (_skip > 0)
            sb.Append($" START AT {_skip}");

        sb.Append(';');
        return await ExecuteRawSpatialAsync(sb.ToString(), ct).ConfigureAwait(false);
    }

    private async Task<List<T>> ExecuteRawSpatialAsync(string surql, CancellationToken ct)
    {
        var response = await _provider.Session.RawQuery(surql, _paramBuilder.Parameters, ct).ConfigureAwait(false);
        if (!response.HasErrors && response.Count > 0)
        {
            if (_provider.SessionBase is not null)
            {
                var mapped = _provider.SessionBase.DeserializeMappedPocoResponse<T>(response);
                if (mapped.Count > 0) return mapped;
            }

            var raw = response.GetValue<List<T>>(0);
            if (raw is not null) return raw;
        }
        return [];
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
