using System;
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

    /// <summary>Bulk-insert documents. More efficient than individual Store calls for large batches.</summary>
    Task<int> BulkInsertAsync<T>(IEnumerable<T> documents, int batchSize = 100, CancellationToken ct = default) where T : class;

    /// <summary>Delete all documents of type T matching the predicate. Executes immediately.</summary>
    Task<long> DeleteWhere<T>(Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class;

    /// <summary>Register a document for deletion by its string ID.</summary>
    void Delete<T>(string id) where T : class;

    /// <summary>Register a document for deletion by its long ID.</summary>
    void Delete<T>(long id) where T : class;

    /// <summary>Register a document for deletion by its int ID.</summary>
    void Delete<T>(int id) where T : class;

    /// <summary>Register a document for deletion by its Guid ID.</summary>
    void Delete<T>(Guid id) where T : class;

    /// <summary>Register a document for deletion by an object ID (boxed).</summary>
    void Delete<T>(object id) where T : class;

    /// <summary>Register multiple documents for insert/upsert.</summary>
    void Store<T>(IEnumerable<T> documents) where T : class;

    /// <summary>Register multiple documents for insert/upsert.</summary>
    void Store<T>(params T[] documents) where T : class;

    /// <summary>Register multiple documents of mixed types for insert/upsert.</summary>
    void StoreObjects(IEnumerable<object> documents);

    /// <summary>Register multiple documents of mixed types for deletion.</summary>
    void DeleteObjects(IEnumerable<object> documents);

    /// <summary>Register multiple documents for insert-only.</summary>
    void Insert<T>(IEnumerable<T> documents) where T : class;

    /// <summary>Register multiple documents for insert-only.</summary>
    void Insert<T>(params T[] documents) where T : class;

    /// <summary>Register multiple documents for update-only.</summary>
    void Update<T>(IEnumerable<T> documents) where T : class;

    /// <summary>Register multiple documents for update-only.</summary>
    void Update<T>(params T[] documents) where T : class;

    /// <summary>Hard-delete a document (bypass soft-delete).</summary>
    void HardDelete<T>(T entity) where T : class;

    /// <summary>Hard-delete a document by its string ID.</summary>
    void HardDelete<T>(string id) where T : class;

    /// <summary>Hard-delete a document by its long ID.</summary>
    void HardDelete<T>(long id) where T : class;

    /// <summary>Hard-delete a document by its int ID.</summary>
    void HardDelete<T>(int id) where T : class;

    /// <summary>Hard-delete a document by its Guid ID.</summary>
    void HardDelete<T>(Guid id) where T : class;

    /// <summary>Hard-delete all documents of type T matching the predicate.</summary>
    Task<long> HardDeleteWhere<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class;

    /// <summary>Undo soft-delete for documents matching the predicate.</summary>
    Task<long> UndoDeleteWhere<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate, CancellationToken ct = default) where T : class;

    /// <summary>Register multiple documents of mixed types for insert-only.</summary>
    void InsertObjects(IEnumerable<object> documents);

    /// <summary>Opt-in a type for identity map tracking within this session.</summary>
    void UseIdentityMapFor<T>() where T : class;

    /// <summary>Queue a low-level storage operation for execution during SaveChangesAsync.</summary>
    void QueueOperation(IStorageOperation operation);

    /// <summary>Register an entity with an expected version for optimistic concurrency gating.</summary>
    void UpdateExpectedVersion<T>(T entity, long expectedVersion) where T : class;

    /// <summary>Register an entity with an expected revision for gated updates.</summary>
    void UpdateRevision<T>(T entity, int revision) where T : class;

    /// <summary>Register an entity with an expected revision; skips the update on mismatch instead of throwing.</summary>
    void TryUpdateRevision<T>(T entity, int revision) where T : class;
}
