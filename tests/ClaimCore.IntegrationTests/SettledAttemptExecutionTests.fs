module ClaimCore.IntegrationTests.SettledAttemptExecutionTests

open System
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.TechnicalWitnessTestSupport

let private start (port: IRecoveryStore) operationId =
    match port.Start(operationId, cancellation) |> await with
    | Ok(RecoveryStart.Started(id, _))
    | Ok(RecoveryStart.AlreadyStarted(id, _)) -> id
    | _ -> failtest "Synthetic attempt must be admitted."

let private later () : BusinessContext =
    {
        ObservedUtcInstant = DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero)
        EffectiveBusinessDate = DateOnly(2026, 9, 8)
        TimeZoneId = "Etc/UTC"
    }

let private rejectThenRetry source witness principal request =
    let preparer =
        actorContext source witness principal EndpointAction.PrepareNewCase request

    (recovery source witness preparer).Retain(draft request preparer, cancellation)
    |> await
    |> accepted
    |> ignore

    let submitter =
        actorContext source witness principal EndpointAction.ExecuteNewCase request

    let port = recovery source witness submitter
    let operation = Operation.prepare request |> accepted
    let attempt = start port request.OperationId
    let decide today current = Claim.decide today request current

    match
        port.ExecuteAdmitted(operation, attempt, clock.Capture, decide, cancellation)
        |> await
    with
    | Ok(AdmittedExecution.Rejected(_, SettlementConfirmation.Confirmed)) -> ()
    | _ -> failtest "Future assertion must have a definite original rejection."

    let mutable calls = 0

    let retry date current =
        calls <- calls + 1
        decide date current

    match port.ExecuteAdmitted(operation, attempt, later, retry, cancellation) |> await with
    | Error RecoveryStoreFailure.NotFound -> ()
    | _ -> failtest "A settled attempt cannot acquire new execution authority."

    Expect.equal calls 0 "No Domain decision after terminal attempt admission"

    Expect.isNone
        (witness.EvidenceStore.TryReadEvidence(request.OperationId, Intent))
        "No orphan acceptance intent"

    let fresh = start port request.OperationId
    Expect.notEqual fresh attempt "A new attempt owns its own identity"

    match port.ExecuteAdmitted(operation, fresh, later, decide, cancellation) |> await with
    | Ok(AdmittedExecution.Accepted _) -> ()
    | _ -> failtest "A fresh attempt can accept the unchanged request on its valid date."

    match port.ExecuteAdmitted(operation, attempt, later, retry, cancellation) |> await with
    | Ok(AdmittedExecution.ObservedAccepted receipt) ->
        Expect.equal receipt.OperationId request.OperationId "Exact observed receipt"
    | _ -> failtest "Accepted authority takes precedence without rewriting the old attempt."

    Expect.equal calls 0 "Receipt observation cannot call Domain"
    attempt

let tests =
    testCase
        "[CC-REC-001] a settled rejection cannot be re-executed or issue an orphan intent"
        (fun () ->
            setup (fun owner source _ witness principal ->
                let request =
                    { newRequest () with
                        Command =
                            Command.Open
                                { registration with
                                    IncidentNotificationDate = "2026-09-08"
                                }
                    }

                let original = rejectThenRetry source witness principal request

                Expect.equal
                    (rowCount owner "case_changes" request.OperationId)
                    1L
                    "Fresh attempt accepts once"

                use connection = new Npgsql.NpgsqlConnection(owner)
                connection.Open()

                use recorded =
                    new Npgsql.NpgsqlCommand(
                        "SELECT outcome FROM claimcore.request_submission_settlements WHERE attempt_id=@attempt",
                        connection
                    )

                Sql.uuid recorded "attempt" original

                match recorded.ExecuteScalar() with
                | :? string as outcome ->
                    Expect.equal outcome "REJECTED" "Original knowledge remains immutable"
                | _ -> failtest "Original settlement is required."))
