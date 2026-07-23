using System.Linq.Expressions;

namespace AeroDB.Sable;

internal static class EncryptedQueryGuard
{
    internal static void Validate(Expression expression, SchemaOptions schema)
    {
        new EncryptedMemberVisitor(schema).Visit(expression);
    }

    private sealed class EncryptedMemberVisitor(SchemaOptions schema) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            var sourceType = node.Expression?.Type;
            if (sourceType is not null
                && EncryptedFieldResolver.Find(sourceType, node.Member.Name, schema) is not null)
            {
                throw new SableEncryptedOperationNotSupportedException(
                    sourceType,
                    $"server-side query over encrypted field '{node.Member.Name}'");
            }

            return base.VisitMember(node);
        }
    }
}
