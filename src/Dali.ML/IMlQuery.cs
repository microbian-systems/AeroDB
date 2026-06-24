using System.Linq.Expressions;

namespace Dali;

public interface IMlQuery<TInput, TOutput>
    where TInput : class
    where TOutput : class
{
    IMlQuery<TInput, TOutput> Model(string name, string version);
    IMlQuery<TInput, TOutput> Input(Expression<Func<TInput, object>> inputMapping);
    IMlQuery<TInput, TOutput> Where(Expression<Func<TInput, bool>> predicate);
    IMlQuery<TInput, TOutput> Take(int limit);
    IMlQuery<TInput, TOutput> Skip(int count);
    Task<TOutput> ComputeAsync(TInput input, CancellationToken ct = default);
    Task<List<TOutput>> ComputeAllAsync(CancellationToken ct = default);
}
