module ClaimCore.IntegrationTests.CaseTombstoneTerminalApprovalTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneEvidence
open ClaimCore.IntegrationTests.CaseTombstoneTerminalApprovalFixture

let private ct = CancellationToken.None

let private assertConflictingDrafts (runtime: Runtime) first second draft expiry firstId =
    let copy = TombstoneTerminalProposal.copy draft

    let changed =
        TombstoneTerminalProposal.ConfirmManagedPayloadAbsence
            { copy with
                PolicyId = "synthetic-changed-policy"
            }

    let mismatch =
        TombstoneWriteOutcome.Refused ClaimCore.Domain.LifecycleRefusal.ApprovalMismatch

    Expect.equal (approve runtime first changed firstId expiry) mismatch "Same ID is immutable"

    Expect.equal
        (approve runtime first draft (Guid.NewGuid()) expiry)
        mismatch
        "One human has one slot"

    Expect.equal
        (approve runtime second changed (Guid.NewGuid()) expiry)
        mismatch
        "One event has one draft"

    let crossAction =
        TombstoneTerminalProposal.CompleteSuppressionHorizon
            {
                Copy =
                    { copy with
                        ExpectedWriterGeneration = 2L
                    }
                RecoveryFenceDigest = String.replicate 64 "c"
                OldWriterGeneration = 1L
                NewWriterGeneration = 2L
            }

    Expect.equal
        (approve runtime second crossAction (Guid.NewGuid()) expiry)
        (TombstoneWriteOutcome.Refused ClaimCore.Domain.LifecycleRefusal.VersionConflict)
        "A different action cannot join a pending event"

let private assertPendingPhase (fixture: PruneFixture) =
    use connection = new NpgsqlConnection(fixture.Owner)
    connection.Open()

    use status =
        new NpgsqlCommand(
            "SELECT phase FROM claimcore.case_erasure_tombstones WHERE case_id=@case",
            connection
        )

    Sql.uuid status "case" fixture.CaseId

    let phase =
        match status.ExecuteScalar() with
        | :? string as value -> value
        | _ -> failtest "Tombstone phase was unavailable"

    Expect.equal phase "ERASURE_PENDING" "Draft is not certification"

let private twoStewards =
    testCase
        "[CC-ERASE-001] terminal draft has two witnessed stewards without phase promotion"
        (fun _ ->
            withPruned (fun fixture _ runtime proposer first second _ ->
                let draft =
                    TombstoneTerminalProposal.ConfirmManagedPayloadAbsence(proposal fixture)

                let expiry = (TombstoneTerminalProposal.copy draft).ValidUntil.AddMinutes(-1.0)
                let firstId = Guid.NewGuid()
                approve runtime first draft firstId expiry |> applied firstId
                approve runtime first draft firstId expiry |> applied firstId
                assertConflictingDrafts runtime first second draft expiry firstId
                let secondId = Guid.NewGuid()
                approve runtime second draft secondId expiry |> applied secondId

                Expect.equal
                    (approve runtime proposer draft (Guid.NewGuid()) expiry)
                    (TombstoneWriteOutcome.Refused
                        ClaimCore.Domain.LifecycleRefusal.ApprovalCapacityExceeded)
                    "A third steward cannot grow approval set"

                assertPendingPhase fixture
                fullAudit fixture))

let private deniedActorAndExpiry =
    testCase "[CC-ERASE-001] terminal approval hides ungranted case and refuses expiry" (fun _ ->
        withPruned (fun fixture _ runtime _ first _ ungranted ->
            let draft = TombstoneTerminalProposal.ConfirmManagedPayloadAbsence(proposal fixture)
            let copy = TombstoneTerminalProposal.copy draft

            Expect.equal
                (approve
                    runtime
                    ungranted
                    draft
                    (Guid.NewGuid())
                    (copy.ValidUntil.AddMinutes(-1.0)))
                TombstoneWriteOutcome.ResourceUnavailable
                "Ungranted actor learns no case existence"

            Expect.equal
                (approve runtime first draft (Guid.NewGuid()) DateTimeOffset.UnixEpoch)
                (TombstoneWriteOutcome.Refused ClaimCore.Domain.LifecycleRefusal.InvalidTime)
                "Expired approval is not persisted"

            let missing =
                TombstoneTerminalProposal.ConfirmManagedPayloadAbsence
                    { copy with CaseId = Guid.NewGuid() }

            Expect.equal
                (approve runtime first missing (Guid.NewGuid()) (copy.ValidUntil.AddMinutes(-1.0)))
                TombstoneWriteOutcome.ResourceUnavailable
                "Absent and inaccessible are identical"))

let private revokedGrant =
    testCase "[CC-ERASE-001] revoked steward cannot add a terminal approval" (fun _ ->
        withPruned (fun fixture (source, _) runtime proposer first _ _ ->
            let draft = TombstoneTerminalProposal.ConfirmManagedPayloadAbsence(proposal fixture)
            let expiry = (TombstoneTerminalProposal.copy draft).ValidUntil.AddMinutes(-1.0)
            let firstId = Guid.NewGuid()
            approve runtime first draft firstId expiry |> applied firstId
            let registry = new ActorGrantRegistry(source, fixture.Witness)
            let actor = actorId (new ActorGrantStore(source)) first

            registry.SetGrant(
                proposer,
                actor,
                {
                    Role = Role.DataSteward
                    Scope = GrantScope.Installation
                },
                false
            )
            |> await
            |> ActorGrantTestSupport.applied

            Expect.equal
                (approve runtime first draft (Guid.NewGuid()) expiry)
                TombstoneWriteOutcome.ResourceUnavailable
                "Revoked grant is checked at each actor-bound approval"

            fullAudit fixture))

let private holdsBoundApproval =
    testCase
        "[CC-ERASE-001] active hold blocks new terminal approval without rewriting old one"
        (fun _ ->
            withPruned (fun fixture _ runtime _ first _ _ ->
                let draft =
                    TombstoneTerminalProposal.ConfirmManagedPayloadAbsence(proposal fixture)

                let expiry = (TombstoneTerminalProposal.copy draft).ValidUntil.AddMinutes(-1.0)
                let holdId = Guid.NewGuid()
                let reviewOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)

                changeHold
                    runtime
                    first
                    fixture.CaseId
                    (TombstoneHoldMutation.Record(holdId, "LEGAL_RETENTION", reviewOn))

                let held = reviewed runtime first fixture.CaseId

                let heldDraft =
                    TombstoneTerminalProposal.ConfirmManagedPayloadAbsence
                        { TombstoneTerminalProposal.copy draft with
                            ExpectedAuthorityRevision = held.AuthorityRevision
                            ExpectedAuthorityHash = held.AuthorityHash
                        }

                let deniedId = Guid.NewGuid()

                Expect.equal
                    (approve runtime first heldDraft deniedId expiry)
                    (TombstoneWriteOutcome.Refused ClaimCore.Domain.LifecycleRefusal.HoldActive)
                    "Active hold prevents a new terminal approval"

                Expect.isNone
                    (fixture.Witness.EvidenceStore.TryReadEvidence(deniedId, Intent))
                    "Hold refusal wrote no witness intent"

                changeHold
                    runtime
                    first
                    fixture.CaseId
                    (TombstoneHoldMutation.Release(holdId, "LEGAL_RELEASE"))

                let current = reviewed runtime first fixture.CaseId

                let revised =
                    TombstoneTerminalProposal.ConfirmManagedPayloadAbsence
                        { TombstoneTerminalProposal.copy draft with
                            ExpectedAuthorityRevision = current.AuthorityRevision
                            ExpectedAuthorityHash = current.AuthorityHash
                        }

                let acceptedId = Guid.NewGuid()
                approve runtime first revised acceptedId expiry |> applied acceptedId

                changeHold
                    runtime
                    first
                    fixture.CaseId
                    (TombstoneHoldMutation.Record(Guid.NewGuid(), "LEGAL_RETENTION", reviewOn))

                fullAudit fixture))

let private changedApprovalEvidence =
    testCase "[CC-ERASE-001] changed terminal approval proof quarantines full audit" (fun _ ->
        withPruned (fun fixture _ runtime _ first _ _ ->
            let draft = TombstoneTerminalProposal.ConfirmManagedPayloadAbsence(proposal fixture)
            let expiry = (TombstoneTerminalProposal.copy draft).ValidUntil.AddMinutes(-1.0)
            let approvalId = Guid.NewGuid()
            approve runtime first draft approvalId expiry |> applied approvalId
            fullAudit fixture
            use connection = new NpgsqlConnection(fixture.Owner)
            connection.Open()

            use tamper =
                new NpgsqlCommand(
                    "UPDATE claimcore.case_erasure_terminal_approvals SET "
                    + "candidate_sha256=decode(repeat('aa',32),'hex') WHERE approval_id=@approval",
                    connection
                )

            Sql.uuid tamper "approval" approvalId
            Expect.equal (tamper.ExecuteNonQuery()) 1 "One synthetic approval was changed"

            Expect.throws
                (fun () -> fullAudit fixture)
                "Changed terminal approval must close full data audit"))

let tests =
    testList
        "terminal erasure draft approvals"
        [
            twoStewards
            deniedActorAndExpiry
            revokedGrant
            holdsBoundApproval
            changedApprovalEvidence
        ]
