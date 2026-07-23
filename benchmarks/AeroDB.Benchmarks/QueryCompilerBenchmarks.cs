using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;

namespace AeroDB.Benchmarks;

/// <summary>
/// Compares the current visitor's cold and reused translation paths. The same
/// pre-built expression is used in both cases so expression construction is not
/// part of the measurement.
/// </summary>
[BenchmarkCategory("QueryCompiler")]
[MemoryDiagnoser(displayGenColumns: false)]
public class QueryTranslationBenchmarks
{
    private Expression _expression = null!;
    private SurrealExpressionVisitor _reusedVisitor = null!;
    private int _minimumNumber = 100;
    private string _namePrefix = "A";
    private int _skip = 20;
    private int _take = 50;

    [GlobalSetup]
    public void Setup()
    {
        var source = Array.Empty<BenchDoc>().AsQueryable();
        _expression = source
            .Where(document => document.Number >= _minimumNumber
                               && document.Name.StartsWith(_namePrefix))
            .OrderByDescending(document => document.CreatedAt)
            .ThenBy(document => document.Name)
            .Skip(_skip)
            .Take(_take)
            .Expression;
        _reusedVisitor = new SurrealExpressionVisitor();
    }

    [Benchmark(Baseline = true)]
    public SurrealQueryResult Translate_with_new_visitor()
        => new SurrealExpressionVisitor().Translate(_expression);

    [Benchmark]
    public SurrealQueryResult Translate_with_reused_visitor()
        => _reusedVisitor.Translate(_expression);
}

/// <summary>
/// Separates rendering from translation and captures the current cold concrete
/// compiled-query path and warm interface-plan cache lookup.
/// </summary>
[BenchmarkCategory("QueryCompiler")]
[MemoryDiagnoser(displayGenColumns: false)]
public class QueryCompilerPipelineBenchmarks
{
    private Expression _expression = null!;
    private SurrealQueryResult _translated = null!;
    private IDocumentStore _store = null!;
    private Expression<Func<IQueryable<BenchDoc>, IQueryable<BenchDoc>>> _concreteQuery = null!;
    private BenchDocsAboveNumber _interfaceQuery = null!;
    private int _minimumNumber = 100;
    private string _namePrefix = "A";

    [GlobalSetup]
    public void Setup()
    {
        var source = Array.Empty<BenchDoc>().AsQueryable();
        _expression = source
            .Where(document => document.Number >= _minimumNumber
                               && document.Name.StartsWith(_namePrefix))
            .OrderBy(document => document.Name)
            .Take(50)
            .Expression;
        _translated = new SurrealExpressionVisitor().Translate(_expression);

        _store = Documents.For(_ => { });
        _concreteQuery = query => query
            .Where(document => document.Number >= _minimumNumber)
            .OrderBy(document => document.Name)
            .Take(50);

        _interfaceQuery = new BenchDocsAboveNumber { MinimumNumber = _minimumNumber };
        _ = CompiledQueryPlanner.GetOrBuildPlan<BenchDoc, IEnumerable<BenchDoc>>(_interfaceQuery);
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _store.DisposeAsync();

    [Benchmark]
    public string Render_pretranslated_query()
        => _translated.ToSurrealQL();

    [Benchmark]
    public string Translate_and_render()
        => new SurrealExpressionVisitor().Translate(_expression).ToSurrealQL();

    [Benchmark]
    public CompiledQuery<BenchDoc> Build_concrete_compiled_query()
        => _store.CompileQuery(_concreteQuery);

    [Benchmark]
    public CompiledPlan Resolve_cached_interface_plan()
        => CompiledQueryPlanner.GetOrBuildPlan<BenchDoc, IEnumerable<BenchDoc>>(_interfaceQuery);
}

public class BenchDocsAboveNumber : ICompiledListQuery<BenchDoc>
{
    public int MinimumNumber { get; set; }

    public Expression<Func<ISableQueryable<BenchDoc>, IEnumerable<BenchDoc>>> QueryIs()
        => query => query
            .Where(document => document.Number >= MinimumNumber)
            .OrderBy(document => document.Name);
}
