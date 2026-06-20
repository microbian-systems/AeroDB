using System.Linq.Expressions;
using TUnit.Core;

namespace Dali.Tests;

public class SearchVectorTests
{
    private IDocumentStore _store = null!;
    private IQuerySession _session = null!;

    [Before(HookType.Test)]
    public async Task Setup()
    {
        _store = await TestHarness.CreateStoreAsync(o =>
        {
            o.Namespace = "test";
            o.Database = "test";
        });
        _session = await _store.QuerySessionAsync();
    }

    [After(HookType.Test)]
    public async Task Cleanup()
    {
        if (_session is not null)
            await _session.DisposeAsync();
        if (_store is not null)
            await _store.DisposeAsync();
    }

    [Test]
    public async Task SurrealFunctions_Score_TranslatesCorrectly()
    {
        // Verify the expression visitor translates Score(n) correctly
        var surql = GetTranslatedSurql<Person>(_session, q =>
            q.Where(p => SurrealFunctions.Score(0) > 0.5));
        surql.ShouldContain("search::score(0)");
    }

    [Test]
    public async Task SurrealFunctions_VectorSimilarityCosine_Translates()
    {
        var vecA = new[] { 1f, 2f };
        var vecB = new[] { 3f, 4f };
        var surql = GetTranslatedSurql<Person>(_session, q =>
            q.Where(p => SurrealFunctions.VectorSimilarityCosine(vecA, vecB) > 0.5));
        surql.ShouldContain("vector::similarity::cosine");
    }

    [Test]
    public async Task SurrealFunctions_CalledDirectly_Throws()
    {
        Should.Throw<NotSupportedException>(() => SurrealFunctions.Score(0));
        Should.Throw<NotSupportedException>(() => SurrealFunctions.VectorDistanceKnn());
        Should.Throw<NotSupportedException>(() => SurrealFunctions.VectorSimilarityCosine([], []));
    }

    [Test]
    public async Task HybridSearchConfig_DefaultsAreCorrect()
    {
        var config = new HybridSearchConfig
        {
            Query = "test",
            QueryVector = [1f, 2f, 3f],
            TextFields = [("Title", 25), ("Content", 10)]
        };
        config.VectorField.ShouldBe("Embedding");
        config.VectorCandidates.ShouldBe(100);
        config.RrfK.ShouldBe(60);
        config.RrfLimit.ShouldBe(80);
    }

    /// <summary>
    /// Helper to extract the translated SurrealQL from a LINQ expression without executing it.
    /// </summary>
    private static string GetTranslatedSurql<T>(IQuerySession session, Func<IQueryable<T>, IQueryable<T>> queryBuilder) where T : class
    {
        var queryable = session.Query<T>();
        var expr = queryBuilder(queryable).Expression;
        var visitor = new SurrealExpressionVisitor();
        var result = visitor.Translate(expr);
        if (string.IsNullOrEmpty(result.TableName))
            result.TableName = typeof(T).Name.ToLowerInvariant();
        return result.ToSurrealQL();
    }
}
