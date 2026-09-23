module ClaimCore.IntegrationTests.PreparationTests

open System
open System.Security.Cryptography
open System.Threading
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures

let private openRuntime () =
    Runtime.OpenPostgres(appConnection (), CancellationToken.None)
    |> await
    |> Result.defaultWith (fun _ -> failtest "Runtime must open for recovery tests.")

let private request operationId reference = openRequest operationId reference

let private prepared (core: IClaimsCore) operationId reference =
    match core.Prepare(request operationId reference, CancellationToken.None) |> await with
    | PrepareOutcome.Prepared(details, review) -> details, review
    | _ -> failtest "Expected a retained typed preparation."

let private expectSqlState expected operation =
    let actual =
        try
            operation ()
            None
        with :? PostgresException as error ->
            Some error.SqlState

    Expect.equal actual (Some expected) "PostgreSQL must reject the prohibited statement"

let private executeRuntime sql =
    use connection = new NpgsqlConnection(appConnection ())
    connection.Open()
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let private setDigest operationId digest =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use command =
        new NpgsqlCommand(
            "UPDATE claimcore.request_preparations SET request_sha256 = @digest WHERE operation_id = @operation",
            connection
        )

    Sql.text command "digest" digest
    Sql.uuid command "operation" operationId
    command.ExecuteNonQuery() |> ignore

let private runtimeReadinessTests =
    testList
        "runtime recovery boundary"
        [
            testCase "runtime admits the exact preparation schema and ACL" (fun () ->
                use connection = new NpgsqlConnection(appConnection ())
                connection.Open()
                RuntimeAcl.requireRole connection
                RuntimeAcl.requireAcl connection
                RuntimeSchema.requireCompatible connection

                Expect.equal
                    (PreparationData.mutationFailure false (TimeoutException()))
                    RecoveryStoreFailure.StoreUnavailable
                    "A technical write failing before commit is definitely uncommitted"

                Expect.equal
                    (PreparationData.mutationFailure true (TimeoutException()))
                    RecoveryStoreFailure.TechnicalMutationUnknown
                    "A write failing after commit start remains explicitly uncertain"

                Expect.equal
                    (PreparationData.mutationFailure false (OperationCanceledException()))
                    RecoveryStoreFailure.CancelledBeforeCommit
                    "Pre-commit caller cancellation is definite"

                Expect.equal
                    (PreparationData.mutationFailure true (OperationCanceledException()))
                    RecoveryStoreFailure.TechnicalMutationUnknown
                    "Commit-start loss overrides caller cancellation"

                let operationId = Guid.NewGuid()

                Expect.equal
                    (StoreData.failure false operationId (TimeoutException()))
                    CoreFailure.StoreUnavailable
                    "Claim failure before commit is definite"

                Expect.equal
                    (StoreData.failure true operationId (TimeoutException()))
                    (CoreFailure.CommitOutcomeUnknown operationId)
                    "Claim failure after commit start is uncertain")
            testCase "runtime cannot alter lineage or recovery lifecycle tables" (fun () ->
                expectSqlState "42501" (fun () ->
                    executeRuntime "INSERT INTO claimcore.installation_lineage DEFAULT VALUES")

                expectSqlState "42501" (fun () ->
                    executeRuntime "DELETE FROM claimcore.request_preparation_lifecycle")

                expectSqlState "42501" (fun () ->
                    executeRuntime "DELETE FROM claimcore.operation_revocations")

                expectSqlState "42501" (fun () ->
                    executeRuntime
                        "UPDATE claimcore.request_submission_settlements SET outcome = 'ERROR'"))
        ]

let private exactPreparationReplay =
    testCase
        "prepare retains exact identity and replay keeps the original technical record"
        (fun () ->
            use runtime = openRuntime ()
            let operationId = Guid.NewGuid()
            let reference = "PREP-" + Guid.NewGuid().ToString("N")
            let first, review = prepared runtime.Core operationId reference
            let second, _ = prepared runtime.Core operationId reference
            Expect.equal first.Summary.OperationId operationId "Retained operation"

            Expect.equal
                second.Summary.PreparedAt
                first.Summary.PreparedAt
                "Exact preparation replay"

            Expect.equal
                first.Summary.State
                PreparationState.Unsubmitted
                "No attempt before resolve"

            Expect.isTrue review.IsAdvisory "Preview is not commit authority"

            let digest =
                first.Summary.RequestSha256
                |> Option.defaultWith (fun () -> failtest "Preparation digest is required.")

            match
                runtime.Core.Recovery.ExportEnvelope(operationId, digest, CancellationToken.None)
                |> await
            with
            | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found artifact) ->
                Expect.equal artifact.RequestSha256 digest "Exact exported digest"
            | _ -> failtest "Expected public recovery export.")

let private corruptedIdentity =
    testCase "corrupt retained identity fails through the public recovery workflow" (fun () ->
        use runtime = openRuntime ()
        let operationId = Guid.NewGuid()

        let details, _ =
            prepared runtime.Core operationId ("PREP-" + Guid.NewGuid().ToString("N"))

        let original =
            details.Summary.RequestSha256
            |> Option.defaultWith (fun () -> failtest "Preparation digest is required.")

        setDigest operationId (String.replicate 64 "b")

        try
            match
                runtime.Core.Recovery.Inspect(
                    operationId,
                    None,
                    recoveryPageLimit,
                    CancellationToken.None
                )
                |> await
            with
            | RecoveryQueryOutcome.RecoveryFailed fault ->
                Expect.equal
                    fault.Code
                    FaultCode.RecoveryIntegrityError
                    "No corrupt preparation view"
            | _ -> failtest "Expected public integrity fault."
        finally
            setDigest operationId original)

let private durableDismissal =
    testCase "dismissal is durable and blocks later public resolution" (fun () ->
        use runtime = openRuntime ()
        let operationId = Guid.NewGuid()

        let details, _ =
            prepared runtime.Core operationId ("PREP-" + Guid.NewGuid().ToString("N"))

        let digest =
            details.Summary.RequestSha256
            |> Option.defaultWith (fun () -> failtest "Preparation digest is required.")

        match
            runtime.Core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
            |> await
        with
        | RecoveryDismissOutcome.DismissedPreparation _ -> ()
        | _ -> failtest "Expected a durable preparation dismissal."

        match
            runtime.Core.Recovery.Resolve(operationId, digest, CancellationToken.None)
            |> await
        with
        | ResolveOutcome.RefusedBeforeAttempt(_, rejection) ->
            Expect.equal rejection.Code RecoveryRejectionCode.OperationRevoked "Dismissal wins"
        | _ -> failtest "Dismissed preparation must not start an attempt.")

let private preparationTests =
    testList
        "public typed preparations"
        [ exactPreparationReplay; corruptedIdentity; durableDismissal ]

let tests =
    testList "PostgreSQL typed recovery" [ runtimeReadinessTests; preparationTests ]
