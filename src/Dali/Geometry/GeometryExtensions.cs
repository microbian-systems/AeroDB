using System.Globalization;

namespace Dali;

internal static class GeometryExtensions
{
    public static string ToInvariantString(this double value)
        => value.ToString(CultureInfo.InvariantCulture);
}
