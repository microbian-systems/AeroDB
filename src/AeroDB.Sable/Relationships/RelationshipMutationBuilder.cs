using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using AeroDB.Sable.Metadata;
using SurrealDb.Net.Models;

namespace AeroDB.Sable;

public static class RelationshipMutationExtensions
{
    public static RelationshipMutationBuilder<T> Relationships<T>(this IDocumentSession session)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(session);

        var options = session is InternalSessionBase internalSession
            ? internalSession.StoreOptions
            : new StoreOptions();

        return new RelationshipMutationBuilder<T>(session, options);
    }
}

public sealed class RelationshipMutationBuilder<T>
    where T : class
{
    private readonly IDocumentSession _session;
    private readonly StoreOptions _options;
    private object? _ownerId;

    internal RelationshipMutationBuilder(IDocumentSession session, StoreOptions options)
    {
        _session = session;
        _options = options;
    }

    public RelationshipMutationBuilder<T> For(object ownerId)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        _ownerId = ownerId;
        return this;
    }

    public Task<int> Add<TRelated>(
        Expression<Func<T, IEnumerable<TRelated>?>> relationship,
        object relatedId,
        CancellationToken ct = default)
        where TRelated : class
    {
        var sql = BuildAddStatement(_options, _ownerId ?? throw MissingOwnerId(), relationship, relatedId);
        return _session.ExecuteSqlAsync(sql, null, ct);
    }

    public Task<int> Remove<TRelated>(
        Expression<Func<T, IEnumerable<TRelated>?>> relationship,
        object relatedId,
        CancellationToken ct = default)
        where TRelated : class
    {
        var sql = BuildRemoveStatement(_options, _ownerId ?? throw MissingOwnerId(), relationship, relatedId);
        return _session.ExecuteSqlAsync(sql, null, ct);
    }

    public Task<int> Set<TRelated>(
        Expression<Func<T, TRelated?>> relationship,
        object relatedId,
        CancellationToken ct = default)
        where TRelated : class
    {
        var sql = BuildSetStatement(_options, _ownerId ?? throw MissingOwnerId(), relationship, relatedId);
        return _session.ExecuteSqlAsync(sql, null, ct);
    }

    public Task<int> Unset<TRelated>(
        Expression<Func<T, TRelated?>> relationship,
        CancellationToken ct = default)
        where TRelated : class
    {
        var sql = BuildUnsetStatement(_options, _ownerId ?? throw MissingOwnerId(), relationship);
        return _session.ExecuteSqlAsync(sql, null, ct);
    }

    internal static string BuildAddStatement<TRelated>(
        StoreOptions options,
        object ownerId,
        Expression<Func<T, IEnumerable<TRelated>?>> relationship,
        object relatedId)
        where TRelated : class
    {
        var source = BuildSource(options, ownerId);
        var field = ResolveRelationshipField(options, relationship);
        var relatedRecord = ToRecordIdLiteral(MetadataDispatch.GetTableName(typeof(TRelated), options.Schema), relatedId);
        return $"UPDATE {source} SET {field} = array::add({field}, {relatedRecord});";
    }

    internal static string BuildRemoveStatement<TRelated>(
        StoreOptions options,
        object ownerId,
        Expression<Func<T, IEnumerable<TRelated>?>> relationship,
        object relatedId)
        where TRelated : class
    {
        var source = BuildSource(options, ownerId);
        var field = ResolveRelationshipField(options, relationship);
        var relatedRecord = ToRecordIdLiteral(MetadataDispatch.GetTableName(typeof(TRelated), options.Schema), relatedId);
        return $"UPDATE {source} SET {field} -= {relatedRecord};";
    }

    internal static string BuildSetStatement<TRelated>(
        StoreOptions options,
        object ownerId,
        Expression<Func<T, TRelated?>> relationship,
        object relatedId)
        where TRelated : class
    {
        var source = BuildSource(options, ownerId);
        var field = ResolveRelationshipField(options, relationship);
        var relatedRecord = ToRecordIdLiteral(MetadataDispatch.GetTableName(typeof(TRelated), options.Schema), relatedId);
        return $"UPDATE {source} SET {field} = {relatedRecord};";
    }

    internal static string BuildUnsetStatement<TRelated>(
        StoreOptions options,
        object ownerId,
        Expression<Func<T, TRelated?>> relationship)
        where TRelated : class
    {
        var source = BuildSource(options, ownerId);
        var field = ResolveRelationshipField(options, relationship);
        return $"UPDATE {source} SET {field} = NONE;";
    }

    private static InvalidOperationException MissingOwnerId()
        => new("Call For(ownerId) before mutating a relationship.");

    private static string BuildSource(StoreOptions options, object ownerId)
        => ToRecordIdLiteral(MetadataDispatch.GetTableName(typeof(T), options.Schema), ownerId);

    private static string ResolveRelationshipField<TRelated>(
        StoreOptions options,
        Expression<Func<T, IEnumerable<TRelated>?>> relationship)
        where TRelated : class
        => ResolveRelationshipField(options, ExtractMember(relationship));

    private static string ResolveRelationshipField<TRelated>(
        StoreOptions options,
        Expression<Func<T, TRelated?>> relationship)
        where TRelated : class
        => ResolveRelationshipField(options, ExtractMember(relationship));

    private static string ResolveRelationshipField(StoreOptions options, MemberInfo member)
    {
        if (options.Schema.Mappings.TryGetValue(typeof(T), out var mapping))
        {
            var configured = mapping.GetRelationshipMappings()
                .FirstOrDefault(r => r.ClrMemberName == member.Name);

            if (configured is not null)
                return configured.StorageFieldName;
        }

        return MetadataDispatch.GetFieldName(typeof(T), member.Name, options.Schema);
    }

    private static MemberInfo ExtractMember<TDelegate>(Expression<TDelegate> expression)
    {
        var body = expression.Body;
        while (body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs } unary)
            body = unary.Operand;

        if (body is MemberExpression member)
            return member.Member;

        throw new ArgumentException("Relationship expression must be a member access.", nameof(expression));
    }

    private static string ToRecordIdLiteral(string tableName, object id)
    {
        ArgumentNullException.ThrowIfNull(id);

        if (id is RecordId recordId)
            return DocumentIdentityResolver.FormatRecordIdLiteral(recordId);

        return DocumentIdentityResolver.FormatRecordIdLiteral(tableName, id);
    }
}
