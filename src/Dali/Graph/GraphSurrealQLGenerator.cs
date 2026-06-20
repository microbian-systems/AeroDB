using System.Text;
using Dali.Metadata;

namespace Dali;

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
}
