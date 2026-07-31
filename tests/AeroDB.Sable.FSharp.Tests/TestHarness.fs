namespace AeroDB.Sable.FSharp.Tests

open System
open System.Threading
open System.Threading.Tasks
open AeroDB.Sable
open SurrealDb.Embedded.InMemory
open SurrealDb.Net

[<RequireQualifiedAccess>]
module TestHarness =
    let mutable private counter = 0

    let private uniqueNamespace () =
        let sequence = Interlocked.Increment(&counter)
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        $"fsharp_test_{sequence}_{suffix}"

    let createStoreAsync () : Task<IDocumentStore> =
        task {
            let namespaceName = uniqueNamespace ()

            let store =
                Documents.For(
                    Action<StoreOptions>(fun options ->
                        options.ClientFactory <-
                            Func<ISurrealDbClient>(fun () -> new SurrealDbMemoryClient())

                        options.Namespace <- namespaceName
                        options.Database <- namespaceName
                        options.Schema.For<TestUser>().SetSchemaMode(SchemaMode.Flexible)
                        |> ignore)
                )

            do! store.InitializeAsync()
            return store
        }
