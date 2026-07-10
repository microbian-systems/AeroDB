using System.Text;
using AeroDB.Sable.Metadata;

namespace AeroDB.Sable;

internal static class GraphSurrealQLGenerator
{
    public static string Generate(GraphQueryPlan plan)
    {
        var sb = new StringBuilder();
        var baseTable = MetadataDispatch.GetTableName(plan.NodeType);

        // Build: SELECT [path expression] FROM baseTable
        sb.Append("SELECT ");

        if (plan.Steps.Count == 0 && !plan.ReturnPath && !plan.CollectAll)
        {
            // No steps: simple SELECT * FROM table
            sb.Append("* FROM `").Append(baseTable).Append('`');
        }
        else
        {
            // Build the traversal expression
            var pathExpr = BuildPathExpression(plan);
            sb.Append(pathExpr);
            sb.Append(" FROM `").Append(baseTable).Append('`');
        }

        // Add WHERE clause
        if (!string.IsNullOrEmpty(plan.FilterSurql))
        {
            sb.Append(" WHERE ").Append(plan.FilterSurql);
        }

        // Add FETCH
        if (plan.FetchRelations is { Length: > 0 })
        {
            sb.Append(" FETCH ").Append(string.Join(", ", plan.FetchRelations));
        }

        sb.Append(';');
        return sb.ToString();
    }

    private static string BuildPathExpression(GraphQueryPlan plan)
    {
        var sb = new StringBuilder();
        bool hasDepthModifier = plan.Depth.HasValue || plan.DepthMin.HasValue || plan.IsUnboundedDepth
                                || plan.ShortestPathTarget != null || plan.IncludeOrigin;

        bool needsDepthWrapper = hasDepthModifier || plan.ReturnPath || plan.CollectAll;

        // Depth/recursion prefix BEFORE the first step
        if (needsDepthWrapper)
        {
            sb.Append("@.{");

            if (plan.IsUnboundedDepth)
            {
                sb.Append("..");
            }
            else if (plan.DepthMin.HasValue && plan.DepthMax.HasValue)
            {
                sb.Append(plan.DepthMin).Append("..").Append(plan.DepthMax);
            }
            else if (plan.Depth.HasValue)
            {
                sb.Append(plan.Depth);
            }
            else if (!hasDepthModifier && (plan.ReturnPath || plan.CollectAll))
            {
                // Path/collect without explicit depth implies unbounded
                sb.Append("..");
            }

            if (plan.ShortestPathTarget != null)
            {
                sb.Append('+').Append("shortest=").Append(plan.ShortestPathTarget);
            }

            if (plan.IncludeOrigin)
            {
                sb.Append("+inclusive");
            }

            // Path and collect at the end of depth prefix
            if (plan.ReturnPath)
                sb.Append("+path");
            else if (plan.CollectAll)
                sb.Append("+collect");

            sb.Append("}");
        }

        // Build arrow chain: ->edge->target OR <-edge<-target
        for (int i = 0; i < plan.Steps.Count; i++)
        {
            var step = plan.Steps[i];

            string forwardArrow = step.Direction switch
            {
                GraphDirection.In => "<-",
                GraphDirection.Out => "->",
                GraphDirection.Both => "<->",
                _ => "->"
            };

            // Edge part
            AppendEdgePart(sb, step, forwardArrow);

            // Intermediate nodes marker (+)
            if (step.IncludeIntermediate)
            {
                sb.Append("(+)");
            }

            // Target part
            AppendTargetPart(sb, step, forwardArrow);
        }

        // If we have steps but no wrapper (depth/return/collect), project the final target
        if (!needsDepthWrapper && plan.Steps.Count > 0)
        {
            sb.Append(".*");
        }

        return sb.ToString();
    }

    private static void AppendEdgePart(StringBuilder sb, GraphStep step, string arrow)
    {
        if (step.EdgeTypes is { Length: > 0 })
        {
            // Multi-edge: ->(e1, e2, ...)
            sb.Append(arrow).Append('(').Append(string.Join(", ", step.EdgeTypes)).Append(')');
        }
        else if (step.EdgeType != null)
        {
            // Single edge: ->edge_type
            sb.Append(arrow).Append(step.EdgeType);
        }
        else
        {
            // Wildcard: ->?
            sb.Append(arrow).Append('?');
        }
    }

    private static void AppendTargetPart(StringBuilder sb, GraphStep step, string arrow)
    {
        if (step.TargetType != null)
        {
            var targetTable = MetadataDispatch.GetTableName(step.TargetType);
            sb.Append(arrow).Append('`').Append(targetTable).Append('`');
        }
        else
        {
            // Wildcard target: ->?
            sb.Append(arrow).Append('?');
        }
    }

    /// <summary>
    /// Generates a graph traversal path expression for use within a SELECT column.
    /// Produces SurrealQL path expressions like "-&gt;product.id" or "&lt;-wrote-post.author.Name".
    /// For simple single-hop traversal with field access.
    /// </summary>
    /// <param name="edgeType">The edge table name (e.g., "wrote_post"). Null for wildcard.</param>
    /// <param name="direction">"out" for forward traversal (-&gt;), "in" for backward traversal (&lt;-).</param>
    /// <param name="targetTable">The target table name (e.g., "product"). Null for wildcard.</param>
    /// <param name="targetField">The field to project from the target (e.g., "id", "name").</param>
    /// <returns>SurrealQL path expression, e.g. "-&gt;product.id" or "&lt;-wrote_post.author.Name".</returns>
    public static string GenerateColumnExpression(
        string? edgeType,
        string direction,
        string? targetTable,
        string targetField)
    {
        var sb = new StringBuilder();
        var arrow = direction switch
        {
            "in" => "<-",
            "out" => "->",
            _ => "->"
        };

        // Edge: ->edgeType or ->?
        if (edgeType is not null)
            sb.Append(arrow).Append(edgeType);
        else
            sb.Append(arrow).Append('?');

        // Target table: ->targetTable or ->?
        if (targetTable is not null)
            sb.Append(arrow).Append(targetTable);
        else
            sb.Append(arrow).Append('?');

        // Field: .fieldName
        sb.Append('.').Append(targetField);

        return sb.ToString();
    }

    /// <summary>
    /// Generates a graph traversal column expression from a <see cref="GraphQueryPlan"/>.
    /// Extracts the final step's edge, direction, target, and appends the specified field.
    /// </summary>
    /// <param name="plan">The graph query plan with traversal steps.</param>
    /// <param name="fieldName">The target field to project.</param>
    /// <returns>SurrealQL path expression for use in a SELECT column.</returns>
    public static string GenerateColumnExpression(GraphQueryPlan plan, string fieldName)
    {
        if (plan.Steps.Count == 0)
            throw new ArgumentException("Plan must have at least one traversal step.", nameof(plan));

        var pathExpr = BuildPathExpression(plan);
        // BuildPathExpression appends .* for non-wrapped queries; we replace that with .fieldName
        if (pathExpr.EndsWith(".*"))
            pathExpr = pathExpr[..^2]; // remove trailing .*

        // Path expression already includes arrows and table names, just append field
        return $"{pathExpr}.{fieldName}";
    }
}
