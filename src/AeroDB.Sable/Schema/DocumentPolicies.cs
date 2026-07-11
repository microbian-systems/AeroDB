namespace AeroDB.Sable;

/// <summary>Global policy system for configuring all document mappings.</summary>
public class DocumentPolicies
{
    private readonly StoreOptions _options;
    internal List<IDocumentPolicy> RegisteredPolicies { get; } = new();

    internal DocumentPolicies(StoreOptions options)
    {
        _options = options;
    }

    /// <summary>Apply an action to every document mapping regardless of type.</summary>
    public void ForAllDocuments(Action<DocumentMapping> configure)
    {
        RegisteredPolicies.Add(new LambdaPolicy(configure));
    }

    /// <summary>Apply an action only to document mappings for a specific type.</summary>
    public void ForDocumentsOfType<T>(Action<DocumentMapping> configure)
    {
        RegisteredPolicies.Add(new TypedLambdaPolicy<T>(configure));
    }

    /// <summary>Add a custom IDocumentPolicy plugin.</summary>
    public void AddPolicy(IDocumentPolicy policy)
    {
        RegisteredPolicies.Add(policy);
    }

    private sealed class LambdaPolicy : IDocumentPolicy
    {
        private readonly Action<DocumentMapping> _configure;
        public LambdaPolicy(Action<DocumentMapping> configure) => _configure = configure;
        public void Apply(DocumentMapping mapping) => _configure(mapping);
    }

    private sealed class TypedLambdaPolicy<T> : IDocumentPolicy
    {
        private readonly Action<DocumentMapping> _configure;
        public TypedLambdaPolicy(Action<DocumentMapping> configure) => _configure = configure;
        public void Apply(DocumentMapping mapping)
        {
            if (mapping.DocumentType == typeof(T))
                _configure(mapping);
        }
    }
}
