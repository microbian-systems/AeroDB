namespace AeroDB.Sable;

/// <summary>
/// Groups experimental feature flags. Access via <c>o.Experimental.ML.Enabled = true</c>.
/// ML support types live in <c>AeroDB.Sable.ML</c> project.
/// </summary>
public class ExperimentalOptions
{
    /// <summary>SurrealML configuration. Types in AeroDB.Sable.ML project.</summary>
    public MlOptions ML { get; } = new();
}
