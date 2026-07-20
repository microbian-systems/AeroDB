using AeroDB.Sable.Configuration;

namespace Microsoft.Extensions.Configuration;

/// <summary>Registration methods for the embedded encrypted Sable provider.</summary>
public static class SableConfigurationBuilderExtensions
{
    /// <summary>Adds the embedded encrypted Sable configuration provider.</summary>
    public static IConfigurationBuilder AddSableConfiguration(
        this IConfigurationBuilder builder,
        Action<SableConfigurationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SableConfigurationOptions();
        configure(options);
        return builder.Add(new SableConfigurationSource(options));
    }

    /// <summary>Adds the embedded encrypted Sable configuration provider.</summary>
    public static IConfigurationBuilder AddSableConfiguration(
        this IConfigurationBuilder builder,
        SableConfigurationOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        return builder.Add(new SableConfigurationSource(options));
    }
}
