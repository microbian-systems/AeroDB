namespace AeroDB.Sable.FSharp

open System
open System.Linq
open System.Threading
open System.Threading.Tasks
open AeroDB.Sable

/// Provider-preserving materializers for standard F# query expressions over Sable.
[<RequireQualifiedAccess>]
module SableQuery =
    [<Literal>]
    let private ProviderRequiredMessage =
        "The query no longer uses the AeroDB.Sable provider and must not be executed."

    /// Requires a query backed by the Sable provider.
    ///
    /// This guard prevents an F# query from silently falling through to synchronous
    /// LINQ-to-Objects materialization after the provider has been lost.
    let requireProvider (source: IQueryable<'T> | null) : ISableQueryable<'T> =
        match source with
        | null -> nullArg (nameof source)
        | :? ISableQueryable<'T> as sableQuery -> sableQuery
        | _ -> invalidOp ProviderRequiredMessage

    /// Returns the generated parameterized Sable command without executing the query.
    let toCommand (source: IQueryable<'T>) : SableCommand =
        source
        |> requireProvider
        |> fun query -> query.ToCommand()

    /// Materializes a Sable query as an immutable F# list.
    let toListAsync
        (cancellationToken: CancellationToken)
        (source: IQueryable<'T>)
        : Task<'T list> =
        task {
            let! values =
                source
                |> requireProvider
                |> fun query -> query.ToListAsync(cancellationToken)

            return List.ofSeq values
        }

    /// Materializes the first row as an F# option.
    ///
    /// Callers must apply deterministic ordering when any matching row is not acceptable.
    let tryFirstAsync<'T when 'T: not struct and 'T: not null>
        (cancellationToken: CancellationToken)
        (source: IQueryable<'T>)
        : Task<'T option> =
        task {
            let! value =
                source
                |> requireProvider
                |> fun query -> query.FirstOrDefaultAsync(cancellationToken)

            return Option.ofObj value
        }

    /// Materializes zero or one row as an F# option and preserves duplicate-row failures.
    let trySingleAsync<'T when 'T: not struct and 'T: not null>
        (cancellationToken: CancellationToken)
        (source: IQueryable<'T>)
        : Task<'T option> =
        task {
            let! value =
                source
                |> requireProvider
                |> fun query -> query.SingleOrDefaultAsync(cancellationToken)

            return Option.ofObj value
        }

    /// Counts matching rows through the Sable provider.
    let countAsync
        (cancellationToken: CancellationToken)
        (source: IQueryable<'T>)
        : Task<int> =
        source
        |> requireProvider
        |> fun query -> query.CountAsync(cancellationToken)

    /// Determines whether a matching row exists through the Sable provider.
    let anyAsync
        (cancellationToken: CancellationToken)
        (source: IQueryable<'T>)
        : Task<bool> =
        source
        |> requireProvider
        |> fun query -> query.AnyAsync(cancellationToken)
