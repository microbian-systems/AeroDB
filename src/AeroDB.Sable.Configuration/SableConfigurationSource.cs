using Microsoft.Extensions.Configuration;

namespace AeroDB.Sable.Configuration;

/// <summary>Configuration source for the embedded encrypted Sable store.</summary>
public sealed class SableConfigurationSource : IConfigurationSource
{
    private readonly SableConfigurationOptions _options;

    /// <summary>Creates a source from explicit bootstrap options.</summary>
    public SableConfigurationSource(SableConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public IConfigurationProvider Build(IConfigurationBuilder builder) =>
        new SableConfigurationProvider(new SableConfigurationStore(_options));
}
