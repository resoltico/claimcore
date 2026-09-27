module ClaimCore.IntegrationTests.CaseErasurePendingTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests

let private cancellation = CancellationToken.None

let private initialRequest actor =
    let input = openRequest (Guid.NewGuid()) ("ERASURE-" + Guid.NewGuid().ToString("N"))
    executeAccepted actor input
    input

let private requestErasure actor input =
    let initial = review actor input.CaseReference

    let request =
        change
            (Guid.NewGuid())
            input.CaseReference
            initial
            (LifecycleMutation.RequestErasure "Synthetic privacy request")

    actor.Lifecycle.Apply(request, cancellation)
    |> await
    |> function
        | LifecycleWriteOutcome.Applied _ -> ()
        | _ -> failtest "Erasure request did not settle."

    review actor input.CaseReference

let private caseId owner reference =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT case_id FROM claimcore.cases WHERE case_reference=@reference",
            connection
        )

    Sql.text command "reference" reference
    command.ExecuteScalar() :?> Guid

let private denialCount owner id =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.case_erasure_operation_denials WHERE case_id=@case",
            connection
        )

    Sql.uuid command "case" id
    command.ExecuteScalar() :?> int64

let private settledRequestAdvances =
    testCase "[CC-ERASE-001] only a settled erasure fence advances to pending" (fun _ ->
        setup (fun _ (source, _) witness runtime proposer _ _ _ _ ->
            let actor = runtime.ForActor proposer
            let input = initialRequest actor
            let requested = requestErasure actor input

            Expect.equal
                requested.PrivacyPhase
                PrivacyPhase.ErasureRequested
                "Request remains a distinct phase"

            let pending =
                change
                    (Guid.NewGuid())
                    input.CaseReference
                    requested
                    (LifecycleMutation.MarkErasurePending "Synthetic fence confirmed")

            match actor.Lifecycle.Apply(pending, cancellation) |> await with
            | LifecycleWriteOutcome.Applied _ -> ()
            | _ -> failtest "Settled fence did not advance."

            Expect.equal
                (review actor input.CaseReference).PrivacyPhase
                PrivacyPhase.ErasurePending
                "Pending does not claim payload absence"

            match actor.Get(input.CaseReference, cancellation) |> await with
            | QueryOutcome.Rejected Rejection.ResourceUnavailable -> ()
            | _ -> failtest "Pending erasure must retain the ordinary-read fence."

            use audit = RuntimeDatabase.openConnection source

            DataAudit.runWithSuppression
                audit
                witness
                (Some(
                    ClaimCore.IntegrationTests.FixturePrivateFiles.syntheticCommitments
                        witness.Identity
                ))
                cancellation
            |> await
            |> ignore))

let private divergentRequestRefuses =
    testCase "[CC-ERASE-001] a mismatched request tombstone cannot mint pending proof" (fun _ ->
        setup (fun owner _ _ runtime proposer _ _ _ _ ->
            let actor = runtime.ForActor proposer
            let input = initialRequest actor
            let requested = requestErasure actor input
            use connection = new NpgsqlConnection(owner)
            connection.Open()

            use tamper =
                new NpgsqlCommand(
                    "UPDATE claimcore.case_erasure_tombstones "
                    + "SET request_candidate_sha256=decode(repeat('ff',32),'hex') "
                    + "WHERE request_event_id IS NOT NULL AND case_id="
                    + "(SELECT case_id FROM claimcore.cases WHERE case_reference=@reference)",
                    connection
                )

            Sql.text tamper "reference" input.CaseReference
            Expect.equal (tamper.ExecuteNonQuery()) 1 "One synthetic fence was altered"

            let pending =
                change
                    (Guid.NewGuid())
                    input.CaseReference
                    requested
                    (LifecycleMutation.MarkErasurePending "Synthetic fence confirmed")

            match actor.Lifecycle.Apply(pending, cancellation) |> await with
            | LifecycleWriteOutcome.Failed CoreFault.StoreIntegrityError -> ()
            | _ -> failtest "Mismatched fence was not refused."

            Expect.equal
                (review actor input.CaseReference).PrivacyPhase
                PrivacyPhase.ErasureRequested
                "No transition can follow forged evidence"))

let private purgeApprovalsDoNotDelete =
    testCase
        "[CC-ERASE-001] two witnessed erasure approvals cannot impersonate owner purge"
        (fun _ ->
            setup (fun _ _ witness runtime proposer first second _ _ ->
                let proposerActor = runtime.ForActor proposer
                let input = initialRequest proposerActor
                let requested = requestErasure proposerActor input

                let pending =
                    change
                        (Guid.NewGuid())
                        input.CaseReference
                        requested
                        (LifecycleMutation.MarkErasurePending "Synthetic fence confirmed")

                proposerActor.Lifecycle.Apply(pending, cancellation) |> await |> ignore
                let current = review proposerActor input.CaseReference

                let purge =
                    change
                        (Guid.NewGuid())
                        input.CaseReference
                        current
                        (LifecycleMutation.PurgeLivePayload(
                            "Synthetic live purge proposal",
                            utcMicrosecond (DateTimeOffset.UtcNow.AddHours 2.0)
                        ))

                match proposerActor.Lifecycle.Apply(purge, cancellation) |> await with
                | LifecycleWriteOutcome.Refused _ -> ()
                | _ -> failtest "Actor invoked owner-only purge."

                let expiry = DateTimeOffset.UtcNow.AddHours 1.0

                for steward in [ first; second ] do
                    let approvalId = Guid.NewGuid()
                    let actor = runtime.ForActor steward

                    match
                        actor.Lifecycle.Approve(purge, approvalId, expiry, cancellation) |> await
                    with
                    | LifecycleWriteOutcome.Applied(id, _, _) when id = approvalId -> ()
                    | _ -> failtest "Purge approval failed."

                    for phase in [ Intent; SettledAuthority ] do
                        let ticket =
                            witness.EvidenceStore.TryReadEvidence(approvalId, phase)
                            |> Option.defaultWith (fun () ->
                                failtest "Purge approval witness missing")
                            |> _.Ticket

                        Expect.equal ticket.ScopeKind Case "Approval remains case-scoped"

                Expect.equal
                    (review proposerActor input.CaseReference).PrivacyPhase
                    PrivacyPhase.ErasurePending
                    "Approvals alone do not claim payload deletion"))

let private witnessIntentDenials =
    testCase "[CC-ERASE-001] complete case witness scan stages bounded HMAC denials" (fun _ ->
        setup (fun owner _ witness runtime proposer _ _ _ _ ->
            let actor = runtime.ForActor proposer
            let input = initialRequest actor
            let requested = requestErasure actor input

            let pending =
                change
                    (Guid.NewGuid())
                    input.CaseReference
                    requested
                    (LifecycleMutation.MarkErasurePending "Synthetic fence confirmed")

            actor.Lifecycle.Apply(pending, cancellation) |> await |> ignore
            let id = caseId owner input.CaseReference
            use connection = new NpgsqlConnection(owner)
            connection.Open()
            use transaction = connection.BeginTransaction()
            let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity
            let cutoff = witness.Snapshot()

            let seal =
                CaseErasureWitnessDenials.scan
                    connection
                    transaction
                    witness
                    commitments
                    id
                    cutoff.TipSequence

            Expect.equal seal.CutoffHash cutoff.TipHash "Full global scan reaches exact tip"
            Expect.isGreaterThan seal.IntentCount 0L "Case intents are not skipped"
            Expect.isGreaterThan seal.DenialCount 0L "Operation denials are retained"
            transaction.Rollback()))

let private orphanIntentRefuses =
    testCase "[CC-ERASE-001] unlinked case intent cannot certify operation coverage" (fun _ ->
        setup (fun owner _ witness runtime proposer _ _ _ _ ->
            let actor = runtime.ForActor proposer
            let input = initialRequest actor
            let requested = requestErasure actor input

            let pending =
                change
                    (Guid.NewGuid())
                    input.CaseReference
                    requested
                    (LifecycleMutation.MarkErasurePending "Synthetic fence confirmed")

            actor.Lifecycle.Apply(pending, cancellation) |> await |> ignore
            let id = caseId owner input.CaseReference
            let before = denialCount owner id

            witness.BeginAuthority(Guid.NewGuid(), [| 0x43uy; 0x43uy; 0x55uy |], Some id)
            |> ignore

            use connection = new NpgsqlConnection(owner)
            connection.Open()
            use transaction = connection.BeginTransaction()
            let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity
            let cutoff = witness.Snapshot()

            Expect.throwsT<CaseIdentityCoverageUnknowable>
                (fun () ->
                    CaseErasureWitnessDenials.scan
                        connection
                        transaction
                        witness
                        commitments
                        id
                        cutoff.TipSequence
                    |> ignore)
                "Unknown orphan may hide a distinct business operation ID"

            transaction.Rollback()
            Expect.equal (denialCount owner id) before "Tentative denials roll back"))

let tests =
    testList
        "settled erasure fence"
        [
            settledRequestAdvances
            divergentRequestRefuses
            purgeApprovalsDoNotDelete
            witnessIntentDenials
            orphanIntentRefuses
        ]
