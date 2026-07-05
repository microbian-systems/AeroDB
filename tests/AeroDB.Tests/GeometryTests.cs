using System.Globalization;
using AeroDB;
using TUnit.Core;

namespace AeroDB.Tests;

/// <summary>
/// Tests for GeometryPoint and GeometryPolygon POCO types.
/// These test the SurrealQL serialization and construction of geometry types.
/// </summary>
public class GeometryTests
{
    // ════════════════════════════════════════════════════════════
    // GeometryPoint
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task GeometryPoint_Default_Constructor()
    {
        var point = new GeometryPoint();

        point.Lng.ShouldBe(0);
        point.Lat.ShouldBe(0);
    }

    [Test]
    public async Task GeometryPoint_Parameterized_Constructor()
    {
        var point = new GeometryPoint(10.5, 20.3);

        point.Lng.ShouldBe(10.5);
        point.Lat.ShouldBe(20.3);
    }

    [Test]
    public async Task GeometryPoint_ToSurrealQL()
    {
        var point = new GeometryPoint(10.5, 20.3);

        var surql = point.ToSurrealQL();

        // Invariant-culture output: decimal point, not comma
        surql.ShouldBe("(10.5, 20.3)");
    }

    [Test]
    public async Task GeometryPoint_ToSurrealQL_Uses_InvariantCulture()
    {
        // Force a culture that uses comma as decimal separator
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var point = new GeometryPoint(10.5, 20.3);

            var surql = point.ToSurrealQL();

            // Must still produce invariant (dot) format
            surql.ShouldBe("(10.5, 20.3)");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public async Task GeometryPoint_Deconstruct()
    {
        var point = new GeometryPoint(10.5, 20.3);

        var (lng, lat) = point;

        lng.ShouldBe(10.5);
        lat.ShouldBe(20.3);
    }

    // ════════════════════════════════════════════════════════════
    // GeometryPolygon
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task GeometryPolygon_Default_Constructor()
    {
        var poly = new GeometryPolygon();

        poly.Rings.ShouldNotBeNull();
        poly.Rings.Count.ShouldBe(0);
    }

    [Test]
    public async Task GeometryPolygon_Single_Ring()
    {
        var ring = new List<(double Lng, double Lat)>
        {
            (0, 0), (1, 0), (1, 1), (0, 1), (0, 0)
        };
        var poly = new GeometryPolygon(ring);

        poly.Rings.Count.ShouldBe(1);
        poly.Rings[0].Count.ShouldBe(5);
    }

    [Test]
    public async Task GeometryPolygon_ToSurrealQL()
    {
        var ring = new List<(double Lng, double Lat)>
        {
            (0, 0), (1, 0), (1, 1), (0, 1), (0, 0)
        };
        var poly = new GeometryPolygon(ring);

        var surql = poly.ToSurrealQL();
        surql.ShouldBe("{ type: 'Polygon', coordinates: [[[0, 0], [1, 0], [1, 1], [0, 1], [0, 0]]] }");
    }

    [Test]
    public async Task GeometryPolygon_With_Holes()
    {
        var exterior = new List<(double Lng, double Lat)>
        {
            (0, 0), (10, 0), (10, 10), (0, 10), (0, 0)
        };
        var hole = new List<(double Lng, double Lat)>
        {
            (2, 2), (8, 2), (8, 8), (2, 8), (2, 2)
        };
        var poly = new GeometryPolygon(exterior, hole);

        poly.Rings.Count.ShouldBe(2);

        var surql = poly.ToSurrealQL();
        surql.ShouldContain("type: 'Polygon'");
        // Both rings present in coordinate array
        surql.ShouldContain("[0, 0]");
        surql.ShouldContain("[2, 2]");
        surql.ShouldContain("[10, 10]");
        surql.ShouldContain("[8, 8]");
    }

    [Test]
    public async Task GeometryPolygon_ToString_Matches_ToSurrealQL()
    {
        var ring = new List<(double Lng, double Lat)>
        {
            (0, 0), (1, 0), (1, 1), (0, 1), (0, 0)
        };
        var poly = new GeometryPolygon(ring);

        poly.ToString().ShouldBe(poly.ToSurrealQL());
    }
}
