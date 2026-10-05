module ClaimCore.IntegrationTests.CaseTombstoneTerminalOwnerTests

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseTombstoneTerminalApprovalFixture
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneEvidence

let private ct = CancellationToken.None

let private syntheticFacts
    (witness: WitnessProtocol)
    caseId
    pruneId
    cutoff
    (cutoffHash: byte array)
    policy
    until
    generation
    (observedAt: DateTimeOffset)
    : OwnerTerminalEvidenceData.CopyAbsenceFacts =
    {
        InstallationId = witness.Identity.InstallationId
        LineageId = witness.Identity.LineageId
        WitnessEpoch = witness.Identity.Epoch
        CaseId = caseId
        PruneEventId = pruneId
        CutoffSequence = cutoff
        CutoffHash = Convert.ToHexStringLower cutoffHash
        InventoryDigest = String.replicate 64 "b"
        RelevantCopyCount = 0L
        WriterGeneration = generation
        PolicyId = policy
        SuppressionUntil = until
        VerifiedAt = observedAt
        ValidUntil = observedAt.AddHours(1.0)
    }

let private syntheticCertificate (witness: WitnessProtocol) facts =
    let tip = (witness.Snapshot(CancellationToken.None).GetAwaiter().GetResult())

    let testProof = SHA256.HashData(Encoding.ASCII.GetBytes("synthetic-zero-copy-only"))
    OwnerCopyAbsenceCertificate.FromVerifiedIssuer(facts, testProof, tip.TipSequence, tip.TipHash)

/// Test-only mechanical issuer. It may certify only an empty isolated fixture; it is never
/// composed by Database/Hosting or treated as off-host signed location evidence.
let private syntheticAbsent (fixture: PruneFixture) =
    { new ICopyErasureCertification with
        member _.RequireAllAbsent
            (
                primary,
                transaction,
                witness,
                caseId,
                pruneId,
                cutoff,
                cutoffHash,
                policy,
                until,
                generation,
                observedAt,
                _
            ) =
            task {
                use command =
                    new NpgsqlCommand(
                        "SELECT count(*) FROM claimcore.managed_copies",
                        primary,
                        transaction
                    )

                let! count = command.ExecuteScalarAsync()

                if count :?> int64 <> 0L || caseId <> fixture.CaseId then
                    return None
                else
                    let facts =
                        syntheticFacts
                            witness
                            caseId
                            pruneId
                            cutoff
                            cutoffHash
                            policy
                            until
                            generation
                            observedAt

                    return Some(syntheticCertificate witness facts)
            }
    }

let private noRecoveryFence =
    { new IRecoveryFenceCertification with
        member _.RequireVerifiedFence(_, _, _, _, _, _, _, _) = task { return None }
    }

let private prepared action =
    withPruned (fun fixture _ runtime _ first second _ ->
        let draft = TombstoneTerminalProposal.ConfirmManagedPayloadAbsence(proposal fixture)
        let expiry = (TombstoneTerminalProposal.copy draft).ValidUntil.AddMinutes(-1.0)

        for steward in [ first; second ] do
            let id = Guid.NewGuid()
            approve runtime steward draft id expiry |> applied id

        fullAudit fixture
        action fixture draft)

let internal withCertified action =
    prepared (fun fixture draft ->
        let eventId = TombstoneTerminalProposal.eventId draft

        match
            CaseTombstoneTerminalOwner.execute
                fixture.Owner
                fixture.Witness
                fixture.Commitments
                (syntheticAbsent fixture)
                noRecoveryFence
                draft
                ct
            |> await
        with
        | OwnerTerminalOutcome.Advanced(id, PrivacyPhase.PayloadErasedSuppressionRetained) when
            id = eventId
            ->
            fullAudit fixture
            action fixture draft
        | _ -> failtest "Synthetic terminal precondition did not advance.")

let private summary (fixture: PruneFixture) =
    use connection = new NpgsqlConnection(fixture.Owner)
    connection.Open()

    DataAudit.runWithSuppression connection fixture.Witness (Some fixture.Commitments) ct
    |> await

let private assertProjection (fixture: PruneFixture) eventId =
    use connection = new NpgsqlConnection(fixture.Owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT (SELECT count(*) FROM claimcore.case_erasure_terminal_events "
            + "WHERE terminal_event_id=@event),"
            + "(SELECT count(*) FROM claimcore.case_erasure_terminal_approval_uses "
            + "WHERE terminal_event_id=@event),"
            + "(SELECT phase FROM claimcore.case_erasure_tombstones WHERE case_id=@case)",
            connection
        )

    Sql.uuid command "event" eventId
    Sql.uuid command "case" fixture.CaseId
    use reader = command.ExecuteReader()
    Expect.isTrue (reader.Read()) "Owner event projection exists"

    Expect.equal
        (reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2))
        (1L, 2L, "PAYLOAD_ERASED_SUPPRESSION_RETAINED")
        "Exactly one technical event consumed exactly two approvals"

let private assertStewardReview (fixture: PruneFixture) =
    match fixture.Steward.Tombstones.Review(fixture.CaseId, ct) |> await with
    | TombstoneReviewOutcome.Available value when
        value.PrivacyPhase = PrivacyPhase.PayloadErasedSuppressionRetained
        && value.WitnessPayloadPruned
        && not value.ManagedCopyCertificationPending
        && value.RequiredDistinctStewardApprovals = 2
        ->
        ()
    | _ -> failtest "Pseudonymous steward status misreported."

let private ownerPhase =
    testCase
        "[CC-ERASE-001] synthetic owner copy phase co-commits witnessed event and uses"
        (fun _ ->
            prepared (fun fixture draft ->
                let issuer = syntheticAbsent fixture

                let execute () =
                    CaseTombstoneTerminalOwner.execute
                        fixture.Owner
                        fixture.Witness
                        fixture.Commitments
                        issuer
                        noRecoveryFence
                        draft
                        ct
                    |> await

                let eventId = TombstoneTerminalProposal.eventId draft

                match execute () with
                | OwnerTerminalOutcome.Advanced(id, PrivacyPhase.PayloadErasedSuppressionRetained) when
                    id = eventId
                    ->
                    ()
                | _ -> failtest "Synthetic owner phase did not advance exactly."

                match execute () with
                | OwnerTerminalOutcome.Advanced(id, PrivacyPhase.PayloadErasedSuppressionRetained) when
                    id = eventId
                    ->
                    ()
                | _ -> failtest "Exact owner terminal retry diverged."

                fullAudit fixture
                let audited = summary fixture

                Expect.equal
                    (audited.TerminalApprovals, audited.TerminalEvents)
                    (2L, 1L)
                    "Full audit counts two exact steward approvals and one owner event"

                assertStewardReview fixture
                assertProjection fixture eventId))

let private missingCopyProof =
    testCase "[CC-ERASE-001] missing all-absent issuer leaves erasure pending" (fun _ ->
        prepared (fun fixture draft ->
            let missing =
                { new ICopyErasureCertification with
                    member _.RequireAllAbsent(_, _, _, _, _, _, _, _, _, _, _, _) =
                        task { return None }
                }

            match
                CaseTombstoneTerminalOwner.execute
                    fixture.Owner
                    fixture.Witness
                    fixture.Commitments
                    missing
                    noRecoveryFence
                    draft
                    ct
                |> await
            with
            | OwnerTerminalOutcome.InventoryUnknown -> ()
            | _ -> failtest "Unknown copies were misclassified."

            Expect.isNone
                ((fixture.Witness.EvidenceStore
                    .TryReadEvidence(
                        TombstoneTerminalProposal.eventId draft,
                        ClaimCore.Witness.Intent,
                        CancellationToken.None
                    )
                    .GetAwaiter()
                    .GetResult()))
                "No terminal witness intent was emitted without copy proof"

            let audited = summary fixture

            Expect.equal
                (audited.TerminalApprovals, audited.TerminalEvents)
                (2L, 0L)
                "Without copy proof no terminal owner event is accepted"))

let tests = testList "terminal owner execution" [ ownerPhase; missingCopyProof ]
