namespace AeroDB;

using System.Linq.Expressions;

/// <summary>
/// Fluent interface for partial document updates (patching).
/// Equivalent to Marten's <c>JasperFx.Events.IPatchExpression&lt;T&gt;</c>.
/// </summary>
/// <typeparam name="T">The document type.</typeparam>
public interface IPatchExpression<T> where T : class
{
    IPatchExpression<T> Set<TValue>(Expression<Func<T, TValue>> property, TValue value);
    IPatchExpression<T> Increment(Expression<Func<T, int>> property, int amount = 1);
    IPatchExpression<T> Increment(Expression<Func<T, long>> property, long amount = 1);
    IPatchExpression<T> Increment(Expression<Func<T, double>> property, double amount = 1);
    IPatchExpression<T> Increment(Expression<Func<T, float>> property, float amount = 1);
    IPatchExpression<T> Increment(Expression<Func<T, decimal>> property, decimal amount = 1);
    IPatchExpression<T> Append<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element);
    IPatchExpression<T> AppendIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element);
    IPatchExpression<T> Insert<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element, int? index = null);
    IPatchExpression<T> InsertIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element, int? index = null);
    IPatchExpression<T> Remove<TElement>(Expression<Func<T, IEnumerable<TElement>>> property, TElement element);
    IPatchExpression<T> Duplicate<TElement>(Expression<Func<T, TElement>> source, params Expression<Func<T, TElement>>[] destinations);
    IPatchExpression<T> Rename(string oldName, Expression<Func<T, object?>> target);
    IPatchExpression<T> Delete<TValue>(Expression<Func<T, TValue>> property);

    /// <summary>Set all writable properties of the matching type to the given value.</summary>
    IPatchExpression<T> SetAll<TValue>(TValue value);

    /// <summary>
    /// Attaches a business reason to this patch operation.
    /// The reason is stored as metadata and available to <see cref="IDocumentSessionListener"/>
    /// implementations and future event pipelines. Does NOT automatically emit domain events.
    /// </summary>
    IPatchExpression<T> WithReason(string reason);
}
