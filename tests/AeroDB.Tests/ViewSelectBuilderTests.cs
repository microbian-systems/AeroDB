using System.Linq.Expressions;
using AeroDB;
using SurrealDb.Net.Models;
using TUnit.Core;

namespace AeroDB.Tests;

// ──────────────────────────────────────────────
// Test entities specific to SELECT builder tests
// ──────────────────────────────────────────────

public class SelectProduct : Record
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Quantity { get; set; }
}

public class SelectReview : Record
{
    public int Rating { get; set; }
    public double Score { get; set; }
    public string ProductId { get; set; } = "";
    public string Title { get; set; } = "";
}

public class SelectPerson : Record
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public class SelectPost : Record
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
}

public class SelectUser : Record
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

/// <summary>
/// Tests for the fluent ViewSelectBuilder and ViewGraphSelectBuilder.
/// </summary>
public class ViewSelectBuilderTests
{
    // ════════════════════════════════════════════════════════════
    //  Section 1 — Aggregates only
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Count_As_Alias()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder.Count().As("total");
        cols.Build().ShouldBe("count() AS total");
    }

    [Test]
    public async Task Sum_As_Alias()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder.Sum(x => x.Rating).As("avg");
        cols.Build().ShouldBe("math::sum(Rating) AS avg");
    }

    [Test]
    public async Task Min_As_Alias()
    {
        var builder = new ViewSelectBuilder<SelectPerson>();
        var cols = builder.Min(x => x.Age).As("youngest");
        cols.Build().ShouldBe("math::min(Age) AS youngest");
    }

    [Test]
    public async Task Max_As_Alias()
    {
        var builder = new ViewSelectBuilder<SelectProduct>();
        var cols = builder.Max(x => x.Price).As("most_expensive");
        cols.Build().ShouldBe("math::max(Price) AS most_expensive");
    }

    [Test]
    public async Task Average_As_Alias()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder.Average(x => x.Score).As("mean");
        cols.Build().ShouldBe("math::mean(Score) AS mean");
    }

    [Test]
    public async Task MultipleAggregates_Chained()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder
            .Count().As("num")
            .Sum(x => x.Rating).As("total")
            .Average(x => x.Score).As("avg_score")
            .Min(x => x.Rating).As("min_rating")
            .Max(x => x.Rating).As("max_rating");
        cols.Build().ShouldBe(
            "count() AS num, math::sum(Rating) AS total, math::mean(Score) AS avg_score, " +
            "math::min(Rating) AS min_rating, math::max(Rating) AS max_rating");
    }

    // ════════════════════════════════════════════════════════════
    //  Section 2 — Column references
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Column_WithAlias()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder.Column(x => x.Title).As("post_title");
        cols.Build().ShouldBe("Title AS post_title");
    }

    [Test]
    public async Task Column_WithoutAlias()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder.Column(x => x.Title);
        cols.Build().ShouldBe("Title");
    }

    [Test]
    public async Task MultipleColumns_Chained()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder
            .Column(x => x.ProductId)
            .Column(x => x.Rating)
            .Column(x => x.Title);
        cols.Build().ShouldBe("ProductId, Rating, Title");
    }

    // ════════════════════════════════════════════════════════════
    //  Section 3 — Columns + aggregates mixed
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task ColumnThenCount_As_Alias()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder
            .Column(x => x.ProductId)
            .Count().As("num");
        cols.Build().ShouldBe("ProductId, count() AS num");
    }

    [Test]
    public async Task CountSumColumn_Chained()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder
            .Count().As("n")
            .Sum(x => x.Rating).As("s")
            .Column(x => x.Title);
        cols.Build().ShouldBe("count() AS n, math::sum(Rating) AS s, Title");
    }

    [Test]
    public async Task MixAggregatesAndColumns_Complex()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder
            .Column(x => x.ProductId)
            .Column(x => x.Title)
            .Count().As("reviews")
            .Average(x => x.Rating).As("avg_rating");
        cols.Build().ShouldBe("ProductId, Title, count() AS reviews, math::mean(Rating) AS avg_rating");
    }

    // ════════════════════════════════════════════════════════════
    //  Section 4 — Graph traversal (single-step)
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Graph_Out_WithEdge_Select()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder
            .Graph().Out<SelectPost>("wrote").Select(x => x.Title).As("post");
        cols.Build().ShouldBe("->wrote->select_post.Title AS post");
    }

    [Test]
    public async Task Graph_In_WithEdge_Select()
    {
        var builder = new ViewSelectBuilder<SelectPerson>();
        var cols = builder
            .Graph().In<SelectUser>("assigned").Select(x => x.Name).As("assignee");
        cols.Build().ShouldBe("<-assigned<-select_user.Name AS assignee");
    }

    [Test]
    public async Task Graph_Out_WildcardEdge_Select()
    {
        var builder = new ViewSelectBuilder<SelectPerson>();
        var cols = builder
            .Graph().Out<SelectPerson>().Select(x => x.Name).As("friend");
        cols.Build().ShouldBe("->?->select_person.Name AS friend");
    }

    [Test]
    public async Task Graph_In_WildcardEdge_Select()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder
            .Graph().In<SelectProduct>().Select(x => x.Name);
        cols.Build().ShouldBe("<-?<-select_product.Name");
    }

    // ════════════════════════════════════════════════════════════
    //  Section 5 — Graph with aggregates in same builder
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Graph_Then_Aggregates()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder
            .Graph().Out<SelectProduct>("sold").Select(x => x.Name).As("product")
            .Count().As("num");
        cols.Build().ShouldBe("->sold->select_product.Name AS product, count() AS num");
    }

    [Test]
    public async Task Aggregate_Then_Graph()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder
            .Count().As("total")
            .Graph().Out<SelectPost>("wrote").Select(x => x.Title).As("post_title");
        cols.Build().ShouldBe("count() AS total, ->wrote->select_post.Title AS post_title");
    }

    // ════════════════════════════════════════════════════════════
    //  Section 6 — Raw SurrealQL expressions
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Raw_WithAlias()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder
            .Raw("array::group(tags)").As("groups");
        cols.Build().ShouldBe("array::group(tags) AS groups");
    }

    [Test]
    public async Task Raw_WithoutAlias()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder
            .Raw("math::round(math::mean(score))");
        cols.Build().ShouldBe("math::round(math::mean(score))");
    }

    [Test]
    public async Task Raw_MixedWithColumns()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder
            .Column(x => x.ProductId)
            .Raw("math::round(math::mean(score))").As("rounded")
            .Count().As("cnt");
        cols.Build().ShouldBe("ProductId, math::round(math::mean(score)) AS rounded, count() AS cnt");
    }

    // ════════════════════════════════════════════════════════════
    //  Section 7 — Guard clauses
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task As_WithoutPendingExpression_Throws()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        Should.Throw<InvalidOperationException>(() => builder.As("x"));
    }

    [Test]
    public async Task As_AfterAs_Throws()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        builder.Count().As("total");
        // Second .As() without a new expression between should throw
        Should.Throw<InvalidOperationException>(() => builder.As("x"));
    }

    [Test]
    public async Task GraphGuardOpen_Throws_WhenTraversingAfterSelect()
    {
        // Even though Select() returns the parent ViewSelectBuilder,
        // the closed ViewGraphSelectBuilder instance still exists and
        // its GuardOpen() should prevent further traversal.
        var builder = new ViewSelectBuilder<SelectReview>();
        var graphBuilder = builder.Graph().Out<SelectPost>("wrote");

        // Close the graph by calling Select (discard the returned parent builder)
        graphBuilder.Select(x => x.Title);

        // Further traversal on the closed builder must throw
        Should.Throw<InvalidOperationException>(() =>
            graphBuilder.Out<SelectUser>("next"));
    }

    [Test]
    public async Task EmptyBuilder_ReturnsStar()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        builder.Build().ShouldBe("*");
    }

    [Test]
    public async Task As_DoubleCall_WithoutNewExpression_Throws()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        builder.Count();
        builder.As("a");
        // No pending alias — should throw
        Should.Throw<InvalidOperationException>(() => builder.As("b"));
    }

    // ════════════════════════════════════════════════════════════
    //  Section 8 — Integration with ViewDefinition
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task ViewDefinition_Select_WithCountAndGroupBy()
    {
        var view = new ViewDefinition<ViewReview>("review_summary")
            .From<ViewReview>()
            .Select(cols => cols
                .Count().As("total")
                .Average(r => r.Rating).As("avg_rating"))
            .GroupBy(r => r.ProductId);

        view.BuildSelectSurql().ShouldBe(
            "SELECT count() AS total, math::mean(Rating) AS avg_rating FROM `view_review` GROUP BY ProductId");
    }

    [Test]
    public async Task ViewDefinition_Select_WithColumnsAndAggregate()
    {
        var view = new ViewDefinition<ViewReview>("product_review_stats")
            .From<ViewReview>()
            .Select(cols => cols
                .Column(r => r.ProductId)
                .Column(r => r.Category)
                .Count().As("cnt"));

        view.BuildSelectSurql().ShouldBe(
            "SELECT ProductId, Category, count() AS cnt FROM `view_review`");
    }

    [Test]
    public async Task ViewDefinition_Select_WithGraphTraversal()
    {
        var view = new ViewDefinition<ViewReview>("review_with_product")
            .From<ViewReview>()
            .Select(cols => cols
                .Column(r => r.Rating)
                .Graph().Out<ViewUser>("product").Select(u => u.Name).As("product_name"));

        view.BuildSelectSurql().ShouldBe(
            "SELECT Rating, ->product->view_user.Name AS product_name FROM `view_review`");
    }

    [Test]
    public async Task ViewDefinition_Select_WithWhereAndGraph()
    {
        var view = new ViewDefinition<ViewReview>("filtered_review_view")
            .From<ViewReview>()
            .Select(cols => cols
                .Count().As("num")
                .Average(r => r.Rating).As("avg")
                .Graph().Out<ViewUser>("author").Select(u => u.Name).As("author"))
            .Where(r => r.Rating >= 3)
            .GroupBy(r => r.Category);

        view.BuildSelectSurql().ShouldBe(
            "SELECT count() AS num, math::mean(Rating) AS avg, ->author->view_user.Name AS author FROM `view_review` WHERE Rating >= 3 GROUP BY Category");
    }

    [Test]
    public async Task ViewDefinition_Select_WithRawExpression()
    {
        var view = new ViewDefinition<ViewReview>("custom_stats")
            .From<ViewReview>()
            .Select(cols => cols
                .Column(r => r.ProductId)
                .Raw("math::round(math::mean(Rating), 2)").As("rounded_avg"));

        view.BuildSelectSurql().ShouldBe(
            "SELECT ProductId, math::round(math::mean(Rating), 2) AS rounded_avg FROM `view_review`");
    }

    // ════════════════════════════════════════════════════════════
    //  Section 9 — .Select() and .WithSelect() independence
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Select_Overrides_WithSelect()
    {
        var view = new ViewDefinition<ViewReview>("test")
            .From<ViewReview>();

        // Set via WithSelect first
        view.WithSelect("old_col");
        view.SelectColumns.ShouldBe("old_col");

        // Set via Select builder — overrides
        view.Select(cols => cols.Count().As("n"));
        view.SelectColumns.ShouldBe("count() AS n");
    }

    [Test]
    public async Task WithSelect_Overrides_Select()
    {
        var view = new ViewDefinition<ViewReview>("test")
            .From<ViewReview>();

        // Set via Select builder first
        view.Select(cols => cols.Count().As("n"));
        view.SelectColumns.ShouldBe("count() AS n");

        // Set via WithSelect — overrides
        view.WithSelect("new_col");
        view.SelectColumns.ShouldBe("new_col");
    }

    // ════════════════════════════════════════════════════════════
    //  Section 10 — Edge cases for member extraction
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Column_WithConvertExpression()
    {
        // Expressions like x => (object)x.Price (boxing) should be unwrapped
        var builder = new ViewSelectBuilder<SelectProduct>();
        var cols = builder.Column(x => (object)x.Price).As("price");
        cols.Build().ShouldBe("Price AS price");
    }

    [Test]
    public async Task Sum_WithConvertExpression()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder.Sum(x => (object)x.Score).As("total_score");
        cols.Build().ShouldBe("math::sum(Score) AS total_score");
    }

    [Test]
    public async Task Graph_Select_StringOverload()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        var cols = builder
            .Graph().Out<SelectPost>("wrote").Select("custom_field");
        cols.Build().ShouldBe("->wrote->select_post.custom_field");
    }

    // ════════════════════════════════════════════════════════════
    //  Section 11 — Build() returns star for no columns
    // ════════════════════════════════════════════════════════════

    [Test]
    public async Task Build_NoColumns_ReturnsStar()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        builder.Build().ShouldBe("*");
    }

    [Test]
    public async Task Build_AfterCountWithoutAlias()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        builder.Count();
        builder.Build().ShouldBe("count()");
    }

    [Test]
    public async Task Build_AfterGraphWithoutAlias()
    {
        var builder = new ViewSelectBuilder<SelectReview>();
        builder.Graph().Out<SelectPost>("wrote").Select(x => x.Title);
        builder.Build().ShouldBe("->wrote->select_post.Title");
    }
}
