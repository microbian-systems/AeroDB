using System.Globalization;

namespace AeroDB.Sable;

internal static class GeometryExtensions
{
    public static string ToInvariantString(this double value)
        => value.ToString(CultureInfo.InvariantCulture);
}
