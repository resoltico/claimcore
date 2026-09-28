module ClaimCore.IntegrationTests.ActorBoundReplayTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private openRuntime app writer =
    Runtime.OpenPostgres(
        app,
        writer,
        witnessKey (),
        suppressionKeyFile (),
        artifactKeyRingFile (),
        CancellationToken.None
    )
    |> await
    |> accepted

let private grantEditor source witness owner target =
    let registry = new ActorGrantRegistry(source, witness)

    let grant =
        {
            Role = Role.CaseEditor
            Scope = GrantScope.Installation
        }

    let id = actorId (new ActorGrantStore(source)) target
    registry.SetGrant(owner, id, grant, true) |> await |> applied
    registry

let private acceptedCount owner operationId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.case_changes WHERE operation_id=@operation",
            connection
        )

    command.Parameters.AddWithValue("operation", operationId) |> ignore
    command.ExecuteScalar() :?> int64

let private crossActorRetained =
    testCase "[CC-AUTH-001] a second editor cannot read another actor's retained draft" (fun _ ->
        withAuthorityRuntimeDatabase (fun owner app writer witness ->
            let first = human "original-preparer"
            let other = human "second-editor"
            provision owner witness first |> applied
            use source = RuntimeDataSource.create app
            let registry = grantEditor source witness first first
            registry.RegisterActor(first, other) |> await |> applied
            grantEditor source witness first other |> ignore
            use runtime = openRuntime app writer
            let firstCore = runtime.ForActor first
            let otherCore = runtime.ForActor other

            let input = openRequest (Guid.NewGuid()) ("SHARED-" + Guid.NewGuid().ToString("N"))

            match firstCore.Prepare(input, CancellationToken.None) |> await with
            | PrepareOutcome.Prepared _ -> ()
            | _ -> failtest "First actor must retain one exact preparation."

            match otherCore.Prepare(input, CancellationToken.None) |> await with
            | PrepareOutcome.PrepareRejected(_, Rejection.ResourceUnavailable) -> ()
            | _ -> failtest "Second editor must not receive the original retained draft."

            match otherCore.Execute(input, CancellationToken.None) |> await with
            | SubmissionOutcome.RejectedBeforeAttempt(None, Rejection.ResourceUnavailable) -> ()
            | _ -> failtest "Second editor cannot submit another actor's pending draft."

            Expect.equal
                (acceptedCount owner input.OperationId)
                0L
                "Cross-actor retry cannot create an accepted event."))

let private crossActorAccepted =
    testCase
        "[CC-AUTH-001] current read grant permits receipt replay without duplicate effect"
        (fun _ ->
            withAuthorityRuntimeDatabase (fun owner app writer witness ->
                let first = human "accepted-preparer"
                let other = human "accepted-reader-editor"
                provision owner witness first |> applied
                use source = RuntimeDataSource.create app
                let registry = grantEditor source witness first first
                registry.RegisterActor(first, other) |> await |> applied
                grantEditor source witness first other |> ignore
                use runtime = openRuntime app writer
                let firstCore = runtime.ForActor first

                let input =
                    openRequest (Guid.NewGuid()) ("REPLAY-" + Guid.NewGuid().ToString("N"))

                match firstCore.Execute(input, CancellationToken.None) |> await with
                | SubmissionOutcome.Completed(_,
                                              _,
                                              DefiniteExecution.Accepted _,
                                              SettlementConfirmation.Confirmed) -> ()
                | _ -> failtest "Original actor must accept exactly once."

                match
                    (runtime.ForActor other).Execute(input, CancellationToken.None) |> await
                with
                | SubmissionOutcome.ObservedAccepted _ -> ()
                | _ -> failtest "Current editor/read grant should observe accepted exact replay."

                Expect.equal
                    (acceptedCount owner input.OperationId)
                    1L
                    "Receipt replay never duplicates the accepted effect."))

let tests = testList "actor-bound replay" [ crossActorRetained; crossActorAccepted ]
