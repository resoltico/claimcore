module ClaimCore.IntegrationTests.RecoveryRaceTests

open System
open System.Threading
open System.Threading.Tasks
open Npgsql
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures

let private openRuntime () =
    witnessedOpen (appConnection ()) CancellationToken.None
    |> await
    |> Result.defaultWith runtimeOpeningFailure

let private prepare (core: IActorClaimsCore) operationId =
    let request = openRequest operationId ("RACE-" + operationId.ToString("N"))

    match core.Prepare(request, CancellationToken.None) |> await with
    | PrepareOutcome.Prepared(details, _) ->
        details.Summary.RequestSha256
        |> Option.defaultWith (fun () -> failtest "Prepared digest is required.")
    | _ -> failtest "Expected a durable synthetic preparation."

let private countFor operationId table =
    use connection = new NpgsqlConnection(appConnection ())
    connection.Open()

    let query =
        match table with
        | "attempts" ->
            "SELECT count(*) FROM claimcore.request_submission_attempts WHERE operation_id = @operation"
        | "settlements" ->
            "SELECT count(*) FROM claimcore.request_submission_settlements s JOIN claimcore.request_submission_attempts a ON a.attempt_id = s.attempt_id WHERE a.operation_id = @operation"
        | "unsettled" ->
            "SELECT count(*) FROM claimcore.request_submission_attempts a WHERE a.operation_id=@operation AND NOT EXISTS (SELECT 1 FROM claimcore.request_submission_settlements s WHERE s.attempt_id=a.attempt_id)"
        | "receipts" ->
            "SELECT count(*) FROM claimcore.case_changes WHERE operation_id = @operation"
        | _ -> invalidArg "table" "Unknown synthetic evidence table."

    use command = new NpgsqlCommand(query, connection)
    command.Parameters.AddWithValue("operation", operationId) |> ignore
    command.ExecuteScalar() :?> int64

let private simultaneous (first: unit -> 'a) (second: unit -> 'b) =
    use barrier = new Barrier(3)

    let launch (action: unit -> 'value) : Task<'value> =
        Task.Run<'value>(
            Func<'value>(fun () ->
                barrier.SignalAndWait() |> ignore
                action ())
        )

    let left = launch first
    let right = launch second
    barrier.SignalAndWait() |> ignore
    left.Result, right.Result

let private acceptedReceipt outcome =
    match outcome with
    | ResolveOutcome.ResolveObservedAccepted receipt -> receipt
    | ResolveOutcome.ResolveCompleted(_,
                                      _,
                                      DefiniteExecution.Accepted receipt,
                                      SettlementConfirmation.Confirmed) -> receipt
    | _ -> failtest "Exact concurrent resolution must observe acceptance."

let private dualResolve =
    testCase "[CC-REC-001] simultaneous exact resolves retain one accepted revision" (fun () ->
        use runtime = openRuntime ()
        let core = runtime.ForActor(ActorBoundStoreFixture.actorPrincipal ())
        let operationId = Guid.NewGuid()
        let digest = prepare core operationId

        let resolve () =
            core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> await

        let first, second = simultaneous resolve resolve
        let left = acceptedReceipt first
        let right = acceptedReceipt second
        Expect.equal left.OperationId right.OperationId "Both return one operation identity"
        Expect.equal left.Snapshot.Version 1L "First revision"
        Expect.equal right.Snapshot.Version 1L "No duplicate revision"
        Expect.equal (countFor operationId "receipts") 1L "One durable receipt"

        let attempts = countFor operationId "attempts"
        Expect.isTrue (attempts = 1L || attempts = 2L) "Each started resolve has an attempt"

        Expect.equal
            (countFor operationId "settlements")
            1L
            "Only the accepting execution settles its attempt"

        Expect.equal
            (countFor operationId "unsettled")
            (attempts - 1L)
            "Observing later acceptance preserves other attempt knowledge")

let private resolveDismiss =
    testCase "[CC-REC-001] simultaneous resolve and dismiss have one lifecycle winner" (fun () ->
        use runtime = openRuntime ()
        let core = runtime.ForActor(ActorBoundStoreFixture.actorPrincipal ())
        let operationId = Guid.NewGuid()
        let digest = prepare core operationId

        let resolve () =
            core.Recovery.Resolve(operationId, digest, CancellationToken.None) |> await

        let dismiss () =
            core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
            |> await

        let resolution, dismissal = simultaneous resolve dismiss

        match resolution, dismissal with
        | ResolveOutcome.ResolveCompleted(_,
                                          _,
                                          DefiniteExecution.Accepted _,
                                          SettlementConfirmation.Confirmed),
          RecoveryDismissOutcome.DismissRefused(_, refusal) ->
            Expect.equal
                refusal.Code
                RecoveryRejectionCode.RecoveryActionUnavailable
                "Acceptance-first prevents revocation"

            Expect.equal (countFor operationId "attempts") 1L "One admitted attempt"
            Expect.equal (countFor operationId "receipts") 1L "One accepted receipt"
            Expect.equal (countFor operationId "settlements") 1L "Accepted attempt is settled"
        | ResolveOutcome.ResolveCompleted(_,
                                          _,
                                          DefiniteExecution.ExecutionRevokedBeforeExecution _,
                                          SettlementConfirmation.Unconfirmed),
          RecoveryDismissOutcome.DismissedPreparation _ ->
            Expect.equal (countFor operationId "attempts") 1L "Started work remains evidence"

            Expect.equal
                (countFor operationId "settlements")
                0L
                "Revocation preserves prior attempt uncertainty"

            Expect.equal (countFor operationId "receipts") 0L "Revocation prevents case mutation"
        | ResolveOutcome.RefusedBeforeAttempt(_, refusal),
          RecoveryDismissOutcome.DismissedPreparation _ ->
            Expect.equal
                refusal.Code
                RecoveryRejectionCode.OperationRevoked
                "Dismiss-first closes future execution authority"

            Expect.equal (countFor operationId "attempts") 0L "No attempt after dismissal"
            Expect.equal (countFor operationId "receipts") 0L "No claim after dismissal"
        | _ -> failtest "Concurrent recovery actions must have one lawful lifecycle winner.")

let tests = testList "PostgreSQL recovery races" [ dualResolve; resolveDismiss ]
