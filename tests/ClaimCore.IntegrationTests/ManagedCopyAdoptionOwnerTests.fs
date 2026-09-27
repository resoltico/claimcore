module ClaimCore.IntegrationTests.ManagedCopyAdoptionOwnerTests

open System
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyAdoptionFixture

let private ct = CancellationToken.None

let private purge owner (witness: WitnessProtocol) change caseId =
    let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity
    let draft = CaseLifecycleCandidate.draft caseId change
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
    | OwnerPurgeOutcome.Purged _ -> commitments
    | _ -> failtest "Synthetic product export live purge failed."

let private approve (runtime: Runtime) proposer (request: CopyAdoptionApprovalRequest) =
    match (runtime.ForActor proposer).ApproveCopyAdoption(request, ct) |> await with
    | CopyAdoptionApprovalOutcome.Approved(id, _) when id = request.ApprovalId -> ()
    | _ -> failtest "Signed custody draft was not approved."

let private adopted owner (witness: WitnessProtocol) commitments privateLocation submission =
    match
        ManagedCopyAdoptionOwner.adopt owner witness commitments privateLocation submission ct
        |> await
    with
    | CopyAdoptionOwnerOutcome.Adopted(id, 2L) when id = submission.AdoptionEventId -> ()
    | _ -> failtest "Signed product export adoption was not applied."

let private inspect owner (witness: WitnessProtocol) commitments copyId eventId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let summary =
        DataAudit.runWithSuppression connection witness (Some commitments) ct |> await

    Expect.equal summary.ErasureFences 1L "Adopted export remains a pending erasure liability"

    use command =
        new NpgsqlCommand(
            "SELECT c.state,c.revision,e.event_kind,first.event_kind "
            + "FROM claimcore.managed_copies c "
            + "JOIN claimcore.managed_copy_events e ON e.copy_id=c.copy_id AND e.revision=2 "
            + "JOIN claimcore.managed_copy_events first "
            + "ON first.copy_id=c.copy_id AND first.revision=1 "
            + "WHERE c.copy_id=@copy AND e.event_id=@event",
            connection
        )

    Sql.uuid command "copy" copyId
    Sql.uuid command "event" eventId
    use reader = command.ExecuteReader()
    Expect.isTrue (reader.Read()) "Signed ADOPT revision exists"

    Expect.equal
        (reader.GetString(0), reader.GetInt64(1))
        ("UNVERIFIED", 2L)
        "Adoption is not absence"

    Expect.equal
        (reader.GetString(2), reader.GetString(3))
        ("ADOPT", "REGISTER")
        "Original export provenance stays immutable"

    Expect.isFalse (reader.Read()) "One signed ADOPT revision is retained"

let private tamper owner (witness: WitnessProtocol) commitments eventId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use change =
        new NpgsqlCommand(
            "UPDATE claimcore.managed_copy_adoptions "
            + "SET inspection_signature=set_byte(inspection_signature,0,"
            + "get_byte(inspection_signature,0) # 1) WHERE adoption_event_id=@event",
            connection
        )

    Sql.uuid change "event" eventId
    Expect.equal (change.ExecuteNonQuery()) 1 "Synthetic inspector signature was changed"

    Expect.throws
        (fun () ->
            DataAudit.runWithSuppression connection witness (Some commitments) ct
            |> await
            |> ignore)
        "Changed independent inspection signature must quarantine full audit"

let private run
    owner
    _
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    _
    _
    _
    _
    change
    artifact
    caseId
    =
    let commitments = purge owner witness change caseId

    let request, submission, privateLocation =
        fixture owner witness runtime proposer caseId artifact

    approve runtime proposer request
    adopted owner witness commitments privateLocation submission
    adopted owner witness commitments privateLocation submission
    inspect owner witness commitments request.CopyId submission.AdoptionEventId
    tamper owner witness commitments submission.AdoptionEventId

let tests =
    testList
        "owner signed copy adoption"
        [
            testCase
                "[CC-ERASE-001] signed product export ADOPT retains original receipt and pending state"
                (fun _ -> CaseErasureArtifactTests.withArtifact run)
        ]
