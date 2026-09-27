module ClaimCore.IntegrationTests.TechnicalWitnessTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.TechnicalWitnessTestSupport

let private prepareCommitThenSettlementFailure =
    testCase
        "[CC-WIT-001] PREPARE commit without settlement stays unknown then reconciles"
        (fun _ ->
            setup (fun owner source writer witness principal ->
                let request =
                    openRequest (Guid.NewGuid()) ("TECH-P-" + Guid.NewGuid().ToString("N"))

                let context =
                    actorContext source witness principal EndpointAction.PrepareNewCase request

                let candidate = draft request context

                use fault =
                    protocol owner writer (fun () -> raise (TimeoutException("synthetic")))

                match
                    (recovery source fault context).Retain(candidate, cancellation) |> await
                with
                | Error RecoveryStoreFailure.TechnicalMutationUnknown -> ()
                | _ -> failtest "Missing PREPARE settlement must remain unknown."

                let eventId = WitnessTechnical.prepareEventId request.OperationId

                Expect.equal
                    (rowCount owner "request_preparations" request.OperationId)
                    1L
                    "Primary PREPARE committed"

                Expect.equal
                    (witnessCount writer (identity owner) eventId)
                    1
                    "Only external PREPARE intent exists"

                use healthy = protocol owner writer (fun () -> ())

                match
                    (recovery source healthy context).Retain(candidate, cancellation) |> await
                with
                | Ok(RecoveryRetain.Existing value) ->
                    Expect.equal value.CaseId candidate.CaseId "Original case ID survives retry"
                | _ -> failtest "Exact PREPARE retry must reconcile the same row."

                Expect.equal
                    (rowCount owner "request_preparations" request.OperationId)
                    1L
                    "Retry never duplicates PREPARE"

                Expect.equal
                    (witnessCount writer (identity owner) eventId)
                    2
                    "Retry appends only settlement"))

let private assertStartReconciled
    owner
    source
    writer
    healthy
    executeContext
    (request: CommandRequest)
    eventId
    =
    match
        (recovery source healthy executeContext).Start(request.OperationId, cancellation)
        |> await
    with
    | Ok(RecoveryStart.AlreadyStarted(attemptId, _)) ->
        Expect.equal attemptId eventId "Exact retry keeps attempt identity"
    | _ -> failtest "Retry must reconcile the same START attempt."

    Expect.equal
        (rowCount owner "request_submission_attempts" request.OperationId)
        1L
        "Retry never duplicates START"

    Expect.equal
        (witnessCount writer (identity owner) eventId)
        2
        "Retry appends only START settlement"

let private startCommitThenSettlementFailure =
    testCase "[CC-WIT-001] START commit without settlement returns same attempt once" (fun _ ->
        setup (fun owner source writer witness principal ->
            let request =
                openRequest (Guid.NewGuid()) ("TECH-S-" + Guid.NewGuid().ToString("N"))

            let prepareContext =
                actorContext source witness principal EndpointAction.PrepareNewCase request

            let candidate = draft request prepareContext
            use healthy = protocol owner writer (fun () -> ())

            match
                (recovery source healthy prepareContext).Retain(candidate, cancellation)
                |> await
            with
            | Ok(RecoveryRetain.Created _) -> ()
            | _ -> failtest "Synthetic PREPARE must commit."

            let executeContext =
                actorContext source witness principal EndpointAction.ExecuteNewCase request

            use fault = protocol owner writer (fun () -> raise (TimeoutException("synthetic")))

            match
                (recovery source fault executeContext).Start(request.OperationId, cancellation)
                |> await
            with
            | Error RecoveryStoreFailure.TechnicalMutationUnknown -> ()
            | Error _ -> failtest "Missing START settlement returned safe failure."
            | Ok _ -> failtest "Missing START settlement must remain unknown."

            let eventId = WitnessTechnical.startEventId request.OperationId 1L

            Expect.equal
                (rowCount owner "request_submission_attempts" request.OperationId)
                1L
                "Primary START committed one attempt"

            Expect.equal
                (witnessCount writer (identity owner) eventId)
                1
                "Only external START intent exists"

            assertStartReconciled owner source writer healthy executeContext request eventId))

let private orphanPrepareIntent =
    testCase "[CC-WIT-001] orphan PREPARE intent never invents a primary preparation" (fun _ ->
        setup (fun owner source writer witness principal ->
            let request =
                openRequest (Guid.NewGuid()) ("TECH-O-" + Guid.NewGuid().ToString("N"))

            let context =
                actorContext source witness principal EndpointAction.PrepareNewCase request

            let candidate = draft request context
            use healthy = protocol owner writer (fun () -> ())
            let eventId, _ = WitnessTechnical.beginPrepare healthy candidate

            match (recovery source healthy context).Retain(candidate, cancellation) |> await with
            | Error RecoveryStoreFailure.TechnicalMutationUnknown -> ()
            | _ -> failtest "Orphan PREPARE intent must remain unknown."

            Expect.equal
                (rowCount owner "request_preparations" request.OperationId)
                0L
                "No primary preparation was fabricated"

            Expect.equal
                (witnessCount writer (identity owner) eventId)
                1
                "Original intent remains for review"))

let private orphanStartIntent =
    testCase "[CC-WIT-001] orphan START intent never invents an attempt" (fun _ ->
        setup (fun owner source writer witness principal ->
            let request =
                openRequest (Guid.NewGuid()) ("TECH-T-" + Guid.NewGuid().ToString("N"))

            let prepareContext =
                actorContext source witness principal EndpointAction.PrepareNewCase request

            use healthy = protocol owner writer (fun () -> ())

            let header =
                match
                    (recovery source healthy prepareContext)
                        .Retain(draft request prepareContext, cancellation)
                    |> await
                with
                | Ok(RecoveryRetain.Created value) -> value
                | _ -> failtest "Synthetic PREPARE must commit."

            let executeContext =
                actorContext source witness principal EndpointAction.ExecuteNewCase request

            let eventId, _ =
                WitnessTechnical.beginStart
                    healthy
                    header
                    1L
                    executeContext.Binding.ActorId
                    "SUBMITTER"
                    executeContext.Binding.GrantRevision

            match
                (recovery source healthy executeContext).Start(request.OperationId, cancellation)
                |> await
            with
            | Error RecoveryStoreFailure.TechnicalMutationUnknown -> ()
            | _ -> failtest "Orphan START intent must remain unknown."

            Expect.equal
                (rowCount owner "request_submission_attempts" request.OperationId)
                0L
                "No primary attempt was fabricated"

            Expect.equal
                (witnessCount writer (identity owner) eventId)
                1
                "Original START intent remains"))

let private forkedPrepareTicket =
    testCase "[CC-WIT-001] forked primary PREPARE ticket refuses exact replay" (fun _ ->
        setup (fun owner source writer witness principal ->
            let request =
                openRequest (Guid.NewGuid()) ("TECH-F-" + Guid.NewGuid().ToString("N"))

            let context =
                actorContext source witness principal EndpointAction.PrepareNewCase request

            let candidate = draft request context
            use healthy = protocol owner writer (fun () -> ())

            match (recovery source healthy context).Retain(candidate, cancellation) |> await with
            | Ok(RecoveryRetain.Created _) -> ()
            | _ -> failtest "Synthetic PREPARE must commit."

            use connection = new NpgsqlConnection(owner)
            connection.Open()

            use tamper =
                new NpgsqlCommand(
                    "UPDATE claimcore.request_preparations SET witness_entry_hash=decode(repeat('ff',32),'hex') "
                    + "WHERE operation_id=@operation",
                    connection
                )

            Sql.uuid tamper "operation" request.OperationId
            Expect.equal (tamper.ExecuteNonQuery()) 1 "One synthetic primary ticket is forked"

            match (recovery source healthy context).Retain(candidate, cancellation) |> await with
            | Error RecoveryStoreFailure.TechnicalMutationUnknown -> ()
            | _ -> failtest "Forked primary ticket must not return an exact replay."

            Expect.equal
                (witnessCount
                    writer
                    (identity owner)
                    (WitnessTechnical.prepareEventId request.OperationId))
                2
                "Independent witness history remains intact"))

let tests =
    testList
        "technical witness phases"
        [
            prepareCommitThenSettlementFailure
            startCommitThenSettlementFailure
            TechnicalWitnessPruneTests.pendingPreparationCannotBePruned
            orphanPrepareIntent
            orphanStartIntent
            forkedPrepareTicket
        ]
