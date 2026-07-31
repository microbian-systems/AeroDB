namespace AeroDB.Sable.FSharp

open System.Threading
open System.Threading.Tasks
open System.Runtime.CompilerServices
open AeroDB.Sable

[<assembly: InternalsVisibleTo("AeroDB.Sable.FSharp.Tests")>]
do ()

/// A document returned by a distance-bearing spatial query.
type SpatialDistance<'T> =
    {
        Document: 'T
        DistanceMeters: double
    }

/// F# materializers for AeroDB.Sable spatial queries.
[<RequireQualifiedAccess>]
module SableSpatialQuery =
    let internal fromDotNet
        (values: seq<AeroDB.Sable.SpatialDistanceResult<'T>>)
        : SpatialDistance<'T> list =
        values
        |> Seq.map (fun value ->
            {
                Document = value.Document
                DistanceMeters = value.DistanceMeters
            })
        |> List.ofSeq

    /// Materializes a distance-bearing spatial query as immutable F# records.
    ///
    /// Only NearBy and OrderByDistance spatial queries can provide a distance.
    /// Provider validation failures and cancellation are preserved from AeroDB.Sable.
    let toListWithDistanceAsync
        (cancellationToken: CancellationToken)
        (query: ISpatialQuery<'T>)
        : Task<SpatialDistance<'T> list> =
        task {
            let! values =
                SpatialExtensions.ToListWithDistanceAsync(query, cancellationToken)

            return fromDotNet values
        }
