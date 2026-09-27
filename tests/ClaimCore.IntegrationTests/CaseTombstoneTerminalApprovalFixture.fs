module internal ClaimCore.IntegrationTests.CaseTombstoneTerminalApprovalFixture

open System
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.CaseWitnessPayloadPruneEvidence

let private ct = CancellationToken.None

let private exactUtc (value: DateTimeOffset) =
    DateTimeOffset(value.UtcTicks - value.UtcTicks % 10L, TimeSpan.Zero)

let proposal (fixture: PruneFixture) =
    let expires = exactUtc (DateTimeOffset.UtcNow.AddHours(1.0))

    {
        EventId = Guid.NewGuid()
        CaseId = fixture.CaseId
        ExpectedAuthorityRevision = fixture.Action.ExpectedAuthorityRevision
        ExpectedAuthorityHash = fixture.Action.ExpectedAuthorityHash
        InstallationId = fixture.Witness.Identity.InstallationId
        LineageId = fixture.Witness.Identity.LineageId
        WitnessEpoch = fixture.Witness.Identity.Epoch
        PruneEventId = fixture.Action.EventId
        WitnessCutoffSequence = fixture.Action.CutoffSequence
        WitnessCutoffHash = fixture.Action.CutoffHash
        CopyInventoryDigest = String.replicate 64 "b"
        RelevantCopyCount = 0L
        ExpectedWriterGeneration = 1L
        PolicyId = "synthetic-reviewed-retention"
        SuppressionUntil = exactUtc (DateTimeOffset.UtcNow.AddDays(30.0))
        ValidUntil = expires
    }

let withPruned action =
    CaseLifecycleStoreFixture.setup
        (fun owner source witness runtime proposer first second ungranted writer ->
            let fixture =
                prepare (fun _ _ -> ()) owner source witness runtime proposer first second writer

            assertFirstPrune fixture
            fullAudit fixture
            action fixture source runtime proposer first second ungranted)

let approve (runtime: Runtime) principal proposal approvalId expiresAt =
    (runtime.ForActor principal).Tombstones.ApproveTerminal(proposal, approvalId, expiresAt, ct)
    |> await

let applied approvalId =
    function
    | TombstoneWriteOutcome.Applied(id, _) when id = approvalId -> ()
    | _ -> failtest "Witnessed terminal draft approval did not apply."

let reviewed (runtime: Runtime) principal caseId =
    match (runtime.ForActor principal).Tombstones.Review(caseId, ct) |> await with
    | TombstoneReviewOutcome.Available value -> value
    | _ -> failtest "Synthetic terminal steward review failed."

let changeHold (runtime: Runtime) principal caseId mutation =
    let status = reviewed runtime principal caseId
    let eventId = Guid.NewGuid()

    let change =
        {
            EventId = eventId
            CaseId = caseId
            ExpectedAuthorityRevision = status.AuthorityRevision
            ExpectedAuthorityHash = status.AuthorityHash
            Mutation = mutation
        }

    match (runtime.ForActor principal).Tombstones.ChangeHold(change, ct) |> await with
    | TombstoneWriteOutcome.Applied(id, _) when id = eventId -> ()
    | _ -> failtest "Synthetic terminal hold mutation failed."
