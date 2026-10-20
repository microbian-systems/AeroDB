using System.Collections.ObjectModel;
using SurrealDb.Net.Models;

namespace AeroDB.Sable;

/// <summary>
/// A finite, parameter-bound record mutation accepted by a document session.
/// </summary>
public sealed class ValidatedRecordMutation
{
    private ValidatedRecordMutation(
        RecordId target,
        IReadOnlyDictionary<string, object?> content,
        ValidatedRecordMutationKind kind)
    {
        Target = target;
        Content = content;
        Kind = kind;
    }

    /// <summary>The record to be created or merged.</summary>
    public RecordId Target { get; }

    /// <summary>The caller-supplied content retained as bound CLR values.</summary>
    public IReadOnlyDictionary<string, object?> Content { get; }

    /// <summary>The finite mutation operation selected by the factory.</summary>
    public ValidatedRecordMutationKind Kind { get; }

    /// <summary>Creates a record with the supplied content.</summary>
    public static ValidatedRecordMutation Create(
        RecordId target,
        IReadOnlyDictionary<string, object?> content)
        => Build(target, content, ValidatedRecordMutationKind.Create);

    /// <summary>Merges the supplied content into an existing record.</summary>
    public static ValidatedRecordMutation MergeExisting(
        RecordId target,
        IReadOnlyDictionary<string, object?> content)
        => Build(target, content, ValidatedRecordMutationKind.MergeExisting);

    private static ValidatedRecordMutation Build(
        RecordId target,
        IReadOnlyDictionary<string, object?> content,
        ValidatedRecordMutationKind kind)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(target.Table);
        if (content.Count == 0)
            throw new ArgumentException("Mutation content cannot be empty.", nameof(content));
        if (content.Keys.Any(key => string.Equals(key, "id", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Mutation content cannot set the record id.", nameof(content));

        var targetId = target.DeserializeId<object>();
        var copiedTarget = RecordId.From(target.Table, targetId);
        var copiedContent = new ReadOnlyDictionary<string, object?>(
            new Dictionary<string, object?>(content, StringComparer.Ordinal));
        return new ValidatedRecordMutation(copiedTarget, copiedContent, kind);
    }
}

/// <summary>Finite operations supported by <see cref="ValidatedRecordMutation"/>.</summary>
public enum ValidatedRecordMutationKind
{
    Create,
    MergeExisting
}
