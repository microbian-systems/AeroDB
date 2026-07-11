namespace AeroDB.Sable;

/// <summary>
/// Internal implementation of <see cref="IAeroSchemaBuilder"/> wrapping <see cref="StoreOptions"/>.
/// Created during <see cref="DocumentStore.InitializeAsync"/> and passed to
/// <see cref="IConfigureAeroDB.Configure(IAeroSchemaBuilder)"/>.
/// </summary>
internal sealed class AeroDBSchemaBuilder : IAeroSchemaBuilder
{
    private readonly StoreOptions _options;

    /// <summary>
    /// Initializes a new instance of <see cref="AeroDBSchemaBuilder"/>.
    /// </summary>
    /// <param name="options">The store options to wrap.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    public AeroDBSchemaBuilder(StoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public DocumentMapping<T> For<T>(Action<DocumentMapping<T>>? configure = null) where T : class
    {
        var mapping = _options.Schema.For<T>();
        configure?.Invoke(mapping);
        return mapping;
    }

    /// <inheritdoc />
    public IEventStreamConfiguration EventStream<T>() where T : class
    {
        return _options.GetOrCreateEventStreamConfig<T>();
    }

    /// <inheritdoc />
    public IProjectionConfiguration Projection<TProjection>() where TProjection : class
    {
        // Phase 4: return _options.GetOrCreateProjectionConfig<TProjection>();
        return new PlaceholderProjectionConfiguration();
    }

    /// <inheritdoc />
    public DocumentPolicies Policies => _options.Policies;

    /// <summary>
    /// Exposes the underlying <see cref="StoreOptions"/> for Phase 2+ internal helpers.
    /// Not part of the public API.
    /// </summary>
    internal StoreOptions Options => _options;

    private sealed class PlaceholderProjectionConfiguration : IProjectionConfiguration
    {
    }
}
