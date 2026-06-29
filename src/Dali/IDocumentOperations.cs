using System.Linq.Expressions;

namespace Dali;

/// <summary>
/// Document CRUD operations shared between <see cref="IDocumentSession"/> and projection operations.
/// Provides the raw document read/write surface without session lifecycle methods.
/// <br/>
/// Note: <c>LoadAsync&lt;T&gt;(id)</c> is available via <c>IQuerySession</c> cast — intentionally omitted
/// to avoid diamond ambiguity with <c>IQuerySession</c> in the <c>IDocumentSession</c> hierarchy.
/// </summary>
public interface IDocumentOperations
{
    /// <summary>Query all documents of type T.</summary>
    Task<IReadOnlyList<T>> QueryAsync<T>(CancellationToken ct = default) where T : class;

    /// <summary>Register a document for insert/upsert during the next SaveChangesAsync.</summary>
    void Store<T>(T document) where T : class;

    /// <summary>Register a document with an explicit ID for insert/upsert.</summary>
    void Store<T>(string id, T document) where T : class;

    /// <summary>Register a document for deletion during the next SaveChangesAsync.</summary>
    void Delete<T>(T document) where T : class;

    /// <summary>Delete all documents of type T matching the predicate. Executes immediately.</summary>
    Task<long> DeleteWhere<T>(Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class;
}
