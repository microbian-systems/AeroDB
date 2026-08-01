namespace AeroDB.Sable.FSharp.Tests

open System
open System.Linq.Expressions
open System.Threading
open System.Threading.Tasks
open AeroDB.Sable
open AeroDB.Sable.FSharp
open Shouldly
open SurrealDb.Embedded.InMemory
open SurrealDb.Net
open TUnit.Core

[<CLIMutable>]
type SpatialPlace =
    {
        Id: int64
        Name: string
        Location: GeometryPoint
    }

type SableSpatialQueryTests() =
    let createStoreAsync () =
        task {
            let namespaceName = $"fsharp_spatial_{Guid.NewGuid():N}"

            let store =
                Documents.For(
                    Action<StoreOptions>(fun options ->
                        options.ClientFactory <-
                            Func<ISurrealDbClient>(fun () -> new SurrealDbMemoryClient())

                        options.Namespace <- namespaceName
                        options.Database <- namespaceName

                        options.Schema.For<SpatialPlace>().SetSchemaMode(SchemaMode.Flexible)
                        |> ignore)
                )

            do! store.InitializeAsync()
            return store
        }

    let locationSelector =
        let place = Expression.Parameter(typeof<SpatialPlace>, "place")
        let location = Expression.Property(place, nameof Unchecked.defaultof<SpatialPlace>.Location)
        let boxedLocation = Expression.Convert(location, typeof<obj>)
        Expression.Lambda<Func<SpatialPlace, obj>>(boxedLocation, place)

    let distanceQuery (session: IQuerySession) =
        SpatialExtensions.Spatial<SpatialPlace>(session)
            .OrderByDistance(locationSelector, 0.0, 0.0)

    [<Test>]
    member _.``Distance materialization returns an immutable F# list of F# records``() =
        task {
            use! store = createStoreAsync ()
            use! session =
                store.OpenSessionAsync(SessionOptions(Tracking = DocumentTracking.None))

            let! actual =
                distanceQuery session
                |> SableSpatialQuery.toListWithDistanceAsync CancellationToken.None

            actual.ShouldBe([] : SpatialDistance<SpatialPlace> list)
        }

    [<Test>]
    member _.``Distance materialization maps document and exact distance into F# records``() =
        let expected =
            {
                Id = 101L
                Name = "Cafe"
                Location = GeometryPoint(2.3522, 48.8566)
            }

        let source =
            [ AeroDB.Sable.SpatialDistanceResult<SpatialPlace>(expected, 42.5) ]

        let actual = SableSpatialQuery.fromDotNet source

        actual.ShouldHaveSingleItem().Document.ShouldBe(expected)
        actual.Head.DistanceMeters.ShouldBe(42.5)

    [<Test>]
    member _.``Distance materialization preserves cancellation``() =
        task {
            use! store = createStoreAsync ()
            use! session =
                store.OpenSessionAsync(SessionOptions(Tracking = DocumentTracking.None))
            use cancellationSource = new CancellationTokenSource()
            cancellationSource.Cancel()

            let action =
                Func<Task>(fun () ->
                    distanceQuery session
                    |> SableSpatialQuery.toListWithDistanceAsync cancellationSource.Token
                    :> Task)

            let! _ = Should.ThrowAsync<OperationCanceledException>(action)
            return ()
        }
