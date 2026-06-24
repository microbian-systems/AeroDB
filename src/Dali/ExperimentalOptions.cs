namespace Dali;

/// <summary>
/// Groups experimental feature flags. Access via <c>o.Experimental.ML.Enabled = true</c>.
/// ML support types live in <c>Dali.ML</c> project.
/// </summary>
public class ExperimentalOptions
{
    /// <summary>SurrealML configuration. Types in Dali.ML project.</summary>
    public MlOptions ML { get; } = new();
}
