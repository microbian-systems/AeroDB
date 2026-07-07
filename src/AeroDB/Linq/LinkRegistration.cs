using System.Linq.Expressions;

namespace AeroDB;

internal sealed record LinkRegistration(
    Type TargetType,
    string TargetTable,
    string FkFieldName,
    bool FkIsRecordId,
    bool CastFkToStringForRecordId = false);

internal sealed record LinkedWhereSpec(
    LambdaExpression Predicate,
    IReadOnlyList<LinkRegistration> Links);
