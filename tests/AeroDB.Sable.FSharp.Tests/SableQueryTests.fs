namespace AeroDB.Sable.FSharp.Tests

open System
open System.Linq
open System.Threading
open AeroDB.Sable
open AeroDB.Sable.FSharp
open Shouldly
open TUnit.Core

type SableQueryTests() =
    let openSessionAsync (store: IDocumentStore) (cancellationToken: CancellationToken) =
        store.OpenSessionAsync(
            SessionOptions(Tracking = DocumentTracking.None),
            cancellationToken
        )

    let seedAsync
        (session: IDocumentSession)
        (users: TestUser list)
        (cancellationToken: CancellationToken)
        =
        task {
            users
            |> List.iter (fun user -> session.Store<TestUser>(user))

            let! _ = session.SaveChangesAsync(cancellationToken)
            return ()
        }

    [<Test>]
    member _.``F# query expressions preserve the Sable provider``() =
        task {
            use! store = TestHarness.createStoreAsync ()
            use! session = openSessionAsync store CancellationToken.None

            let userId = 12345L

            let userQuery =
                query {
                    for user in session.Query<TestUser>() do
                    where (user.Id = userId)
                    select user
                }

            let providerQuery = SableQuery.requireProvider userQuery
            let command = SableQuery.toCommand userQuery

            providerQuery.ShouldBeAssignableTo<ISableQueryable<TestUser>>()
            |> ignore

            command.CommandText.ShouldContain("WHERE")
            command.CommandText.ShouldContain("$p0")
            command.Parameters["p0"].ShouldBe(userId)
        }

    [<Test>]
    member _.``Provider guard rejects LINQ to Objects``() =
        let source = ResizeArray<TestUser>().AsQueryable()

        let action =
            Action(fun () -> SableQuery.requireProvider source |> ignore)

        action.ShouldThrow<InvalidOperationException>() |> ignore

    [<Test>]
    member _.``List materialization executes against embedded SurrealDB and returns an F# list``() =
        task {
            let cancellationToken = CancellationToken.None
            use! store = TestHarness.createStoreAsync ()
            use! session = openSessionAsync store cancellationToken

            let ada = { Id = 42L; Name = "Ada"; Age = 37 }
            let grace = { Id = 43L; Name = "Grace"; Age = 29 }
            do! seedAsync session [ ada; grace ] cancellationToken

            let adults =
                query {
                    for user in session.Query<TestUser>() do
                    where (user.Age >= 30)
                    select user
                }

            let! actual =
                adults |> SableQuery.toListAsync cancellationToken

            actual.ShouldBe([ ada ])
        }

    [<Test>]
    member _.``Optional materializers distinguish persisted rows from absence``() =
        task {
            let cancellationToken = CancellationToken.None
            use! store = TestHarness.createStoreAsync ()
            use! session = openSessionAsync store cancellationToken

            let expected = { Id = 42L; Name = "Ada"; Age = 37 }
            do! seedAsync session [ expected ] cancellationToken

            let existing =
                query {
                    for user in session.Query<TestUser>() do
                    where (user.Id = expected.Id)
                    select user
                }

            let absent =
                query {
                    for user in session.Query<TestUser>() do
                    where (user.Id = -1L)
                    select user
                }

            let! first = existing |> SableQuery.tryFirstAsync cancellationToken
            let! single = absent |> SableQuery.trySingleAsync cancellationToken

            first.ShouldBe(Some expected)
            single.ShouldBe(None)
        }

    [<Test>]
    member _.``Scalar materializers execute against embedded SurrealDB``() =
        task {
            let cancellationToken = CancellationToken.None
            use! store = TestHarness.createStoreAsync ()
            use! session = openSessionAsync store cancellationToken

            do!
                seedAsync
                    session
                    [ { Id = 41L; Name = "Ada"; Age = 37 }
                      { Id = 42L; Name = "Grace"; Age = 29 }
                      { Id = 43L; Name = "Margaret"; Age = 85 } ]
                    cancellationToken

            let experienced =
                query {
                    for user in session.Query<TestUser>() do
                    where (user.Age >= 30)
                    select user
                }

            let namedAda =
                query {
                    for user in session.Query<TestUser>() do
                    where (user.Name = "Ada")
                    select user
                }

            let! count = experienced |> SableQuery.countAsync cancellationToken
            let! exists = namedAda |> SableQuery.anyAsync cancellationToken

            count.ShouldBe(2)
            exists.ShouldBeTrue()
        }

    [<Test>]
    member _.``Embedded materialization observes cancellation``() =
        task {
            use! store = TestHarness.createStoreAsync ()
            use! session = openSessionAsync store CancellationToken.None
            use cancellationSource = new CancellationTokenSource()
            cancellationSource.Cancel()

            let cancellationToken = cancellationSource.Token
            let mutable cancellationObserved = false

            try
                let! _ =
                    session.Query<TestUser>()
                    |> SableQuery.toListAsync cancellationToken

                ()
            with :? OperationCanceledException ->
                cancellationObserved <- true

            cancellationObserved.ShouldBeTrue()
        }
