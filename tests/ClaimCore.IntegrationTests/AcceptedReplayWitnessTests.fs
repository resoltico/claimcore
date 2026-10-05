module ClaimCore.IntegrationTests.AcceptedReplayWitnessTests

open System.Threading
open System
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.TechnicalWitnessTestSupport

let private acceptedWithMissingSettlement owner source writer witness principal request =
    let preparer =
        actorContext source witness principal EndpointAction.PrepareNewCase request

    let material = draft request preparer

    recovery source witness preparer
    |> fun port -> port.Retain(material, cancellation) |> await |> accepted |> ignore

    let submitter =
        actorContext source witness principal EndpointAction.ExecuteNewCase request

    let port = recovery source witness submitter

    let attempt =
        match port.Start(request.OperationId, cancellation) |> await with
        | Ok(RecoveryStart.Started(id, _)) -> id
        | _ -> failtest "Synthetic attempt must be independently admitted."

    use faulty = protocol owner writer (fun () -> raise (TimeoutException()))

    let operation =
        Operation.prepare request
        |> Result.defaultWith (fun _ -> failtest "Valid request")

    match
        (recovery source faulty submitter)
            .ExecuteAdmitted(
                operation,
                attempt,
                clock.Capture,
                (fun today current -> Claim.decide today request current),
                cancellation
            )
        |> await
    with
    | Ok(AdmittedExecution.CommitOutcomeUnknown _) -> ()
    | _ -> failtest "Primary acceptance without witnessed settlement must remain unknown."

    Expect.equal (rowCount owner "case_changes" request.OperationId) 1L "Actual primary COMMIT"
    material, preparer, submitter

let private replay =
    testCase "[CC-WIT-001] accepted replay requires exact independent settlement" (fun () ->
        setup (fun owner source writer witness principal ->
            let request = newRequest ()

            let material, _, submitter =
                acceptedWithMissingSettlement owner source writer witness principal request

            use faulty = protocol owner writer (fun () -> raise (TimeoutException()))

            use uncertain =
                new PostgresStore(source, faulty, submitter, CaseListCursorTestSupport.protection)

            match
                (uncertain :> IClaimStore)
                    .Accepted(request.OperationId, material.RequestSha256, CancellationToken.None)
                |> await
            with
            | Error(CoreFailure.CommitOutcomeUnknown id) ->
                Expect.equal id request.OperationId "Exact identity"
            | _ -> failtest "A primary row alone must not disclose a definite receipt."

            use healthy =
                new PostgresStore(
                    source,
                    witness,
                    submitter,
                    CaseListCursorTestSupport.protection
                )

            match
                (healthy :> IClaimStore)
                    .Accepted(request.OperationId, material.RequestSha256, CancellationToken.None)
                |> await
            with
            | Ok(Some receipt) -> Expect.equal (Claim.view receipt.Case).Version 1L "One revision"
            | _ -> failtest "Healthy exact replay reconciles the committed candidate."

            (witness
                .RequireSettled(request.OperationId, SettledAccepted, CancellationToken.None)
                .GetAwaiter()
                .GetResult())

            Expect.equal
                (rowCount owner "case_changes" request.OperationId)
                1L
                "No duplicate acceptance"))

let private shortcut label start =
    testCase ("[CC-WIT-001] " + label + " cannot bypass accepted settlement") (fun () ->
        setup (fun owner source writer witness principal ->
            let request = newRequest ()

            let material, preparer, submitter =
                acceptedWithMissingSettlement owner source writer witness principal request

            use faulty = protocol owner writer (fun () -> raise (TimeoutException()))

            let result =
                if start then
                    (recovery source faulty submitter).Start(request.OperationId, cancellation)
                    |> await
                    |> Result.map ignore
                else
                    (recovery source faulty preparer).Retain(material, cancellation)
                    |> await
                    |> Result.map ignore

            Expect.equal
                result
                (Error RecoveryStoreFailure.TechnicalMutationUnknown)
                "Independent settlement is mandatory"

            Expect.equal
                (rowCount owner "case_changes" request.OperationId)
                1L
                "No duplicate acceptance"))

let private checkUnknown (request: CommandRequest) result =
    match result with
    | Error(CoreFailure.CommitOutcomeUnknown id) ->
        Expect.equal id request.OperationId "Exact uncertain operation"
    | _ -> failtest "A read cannot disclose a primary-only acceptance."

let private checkRead source witness (request: CommandRequest) context query =
    use claims =
        new PostgresStore(source, witness, context, CaseListCursorTestSupport.protection)

    query (claims :> IClaimStore)
    |> await
    |> Result.map ignore
    |> checkUnknown request

let private caseReads source witness (gate: IActorGate) principal (request: CommandRequest) =
    let read action query =
        let context =
            gate.Case(principal, action, request.CaseReference, cancellation)
            |> await
            |> Option.defaultWith (fun () -> failtest "Read context is authorised.")

        checkRead source witness request context query

    read EndpointAction.GetCase (fun claims ->
        claims.Get(request.CaseReference, CancellationToken.None))

    read EndpointAction.HistorySummary (fun claims ->
        claims.History(request.CaseReference, 0L, CancellationToken.None))

let private otherReads source witness (gate: IActorGate) principal (request: CommandRequest) =
    let context =
        gate.Operation(
            principal,
            EndpointAction.ObserveOperation,
            request.OperationId,
            cancellation
        )
        |> await
        |> Option.defaultWith (fun () -> failtest "Operation context is authorised.")

    checkRead source witness request context (fun claims ->
        claims.Operation(request.OperationId, CancellationToken.None))

    let listing =
        gate.List(principal, cancellation)
        |> await
        |> Option.defaultWith (fun () -> failtest "List context is authorised.")

    checkRead source witness request listing (fun claims ->
        claims.List({ AfterCursor = None; Limit = 10 }, CancellationToken.None))

let private readRefusals =
    testCase
        "[CC-WIT-001] read-only case and receipt disclosures require settled evidence"
        (fun () ->
            setup (fun owner source writer witness principal ->
                let request = newRequest ()

                acceptedWithMissingSettlement owner source writer witness principal request
                |> ignore

                let before =
                    (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())
                        .TipSequence

                let gate =
                    new PostgresActorGate(
                        source,
                        FixturePrivateFiles.syntheticCommitments witness.Identity
                    )
                    :> IActorGate

                caseReads source witness gate principal request
                otherReads source witness gate principal request

                Expect.equal
                    ((witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())
                        .TipSequence)
                    before
                    "Read-only checks append no settlement"))

let tests =
    testList
        "witnessed accepted observation"
        [
            replay
            readRefusals
            shortcut "retention" false
            shortcut "attempt admission" true
        ]
