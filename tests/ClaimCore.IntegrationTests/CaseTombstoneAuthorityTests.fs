module ClaimCore.IntegrationTests.CaseTombstoneAuthorityTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests

let private ct = CancellationToken.None

let private review (actor: IActorClaimsCore) caseId =
    match actor.Tombstones.Review(caseId, ct) |> await with
    | TombstoneReviewOutcome.Available value -> value
    | _ -> failtest "Tombstone review failed."

let private validUntil () =
    let value = DateTimeOffset.UtcNow.AddHours(1.0)
    DateTimeOffset(value.UtcTicks - value.UtcTicks % 10L, TimeSpan.Zero)

let private proposal (value: TombstoneReview) eventId expires =
    {
        EventId = eventId
        CaseId = value.CaseId
        PurgeEventId = value.PurgeEventId
        PurgeWitnessSequence = value.PurgeWitnessSequence
        PurgeWitnessEpoch = value.PurgeWitnessEpoch
        PurgeWitnessHash = value.PurgeWitnessHash
        CutoffSequence = value.CutoffSequence
        CutoffHash = value.CutoffHash
        TargetCount = value.TargetCount
        TargetDigest = value.TargetDigest
        ExpectedAuthorityRevision = value.AuthorityRevision
        ExpectedAuthorityHash = value.AuthorityHash
        ValidUntil = expires
    }

let private requireApplied eventId =
    function
    | TombstoneWriteOutcome.Applied(id, _) when id = eventId -> ()
    | _ -> failtest "Witnessed tombstone action did not apply."

let private assertAudit owner (witness: WitnessProtocol) commitments =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let report =
        DataAudit.runWithSuppression connection witness (Some commitments) ct |> await

    Expect.equal report.ErasureFences 1L "Tombstone authority is full-audited"

let private purgedCase owner (witness: WitnessProtocol) (runtime: Runtime) proposer first second =
    let _, input, purge = CaseErasurePurgeTests.proposal runtime proposer first second
    let caseId = CaseErasurePurgeTests.caseId owner input.CaseReference
    let draft = CaseLifecycleCandidate.draft caseId purge
    let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    match
        CaseErasurePurge.execute
            owner
            connection
            witness
            commitments
            (CaseErasurePurgeTests.syntheticInventory caseId)
            draft
            ct
        |> await
    with
    | OwnerPurgeOutcome.Purged _ -> caseId, commitments
    | _ -> failtest "Synthetic prerequisite purge failed."

let private approvePrune
    (firstActor: IActorClaimsCore)
    (secondActor: IActorClaimsCore)
    (prune: TombstonePruneProposal)
    (expires: DateTimeOffset)
    =
    for actor in [ firstActor; secondActor ] do
        let approvalId = Guid.NewGuid()

        actor.Tombstones.ApproveWitnessPrune(prune, approvalId, expires.AddMinutes(-1.0), ct)
        |> await
        |> requireApplied approvalId

let private assertHeldPruneDenied (runtime: Runtime) proposer (held: TombstoneReview) =
    let blocked = proposal held (Guid.NewGuid()) (validUntil ())

    match
        (runtime.ForActor proposer)
            .Tombstones
            .ApproveWitnessPrune(blocked, Guid.NewGuid(), blocked.ValidUntil.AddMinutes(-1.0), ct)
        |> await
    with
    | TombstoneWriteOutcome.Refused ClaimCore.Domain.LifecycleRefusal.HoldActive -> ()
    | _ -> failtest "Active hold did not refuse prune approval."

let private run owner _ (witness: WitnessProtocol) (runtime: Runtime) proposer first second _ _ =
    let caseId, commitments = purgedCase owner witness runtime proposer first second

    let firstActor = runtime.ForActor first
    let secondActor = runtime.ForActor second
    let initial = review firstActor caseId
    let expires = validUntil ()
    let prune = proposal initial (Guid.NewGuid()) expires

    approvePrune firstActor secondActor prune expires

    assertAudit owner witness commitments

    let holdId = Guid.NewGuid()
    let holdEvent = Guid.NewGuid()

    let hold =
        {
            EventId = holdEvent
            CaseId = caseId
            ExpectedAuthorityRevision = initial.AuthorityRevision
            ExpectedAuthorityHash = initial.AuthorityHash
            Mutation =
                TombstoneHoldMutation.Record(
                    holdId,
                    "LEGAL_RETENTION",
                    DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30)
                )
        }

    firstActor.Tombstones.ChangeHold(hold, ct) |> await |> requireApplied holdEvent

    let held = review firstActor caseId
    Expect.equal held.ActiveHolds.Length 1 "The witnessed hold is active"
    Expect.equal held.AuthorityRevision 1L "Hold advances the tombstone authority tip"

    assertHeldPruneDenied runtime proposer held

    let releaseEvent = Guid.NewGuid()

    let release =
        {
            EventId = releaseEvent
            CaseId = caseId
            ExpectedAuthorityRevision = held.AuthorityRevision
            ExpectedAuthorityHash = held.AuthorityHash
            Mutation = TombstoneHoldMutation.Release(holdId, "LEGAL_RELEASE")
        }

    secondActor.Tombstones.ChangeHold(release, ct)
    |> await
    |> requireApplied releaseEvent

    let released = review firstActor caseId
    Expect.isEmpty released.ActiveHolds "Explicit witnessed release ends the hold"
    Expect.equal released.AuthorityRevision 2L "Release advances the authority tip"
    assertAudit owner witness commitments

let tests =
    testList
        "case tombstone stewardship"
        [
            testCase
                "[CC-ERASE-001] tombstone prune approvals and holds remain witnessed and auditable"
                (fun _ -> CaseLifecycleStoreFixture.setup run)
        ]
