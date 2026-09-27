module ClaimCore.IntegrationTests.ManagedCopyExternalPublicationTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreFixture
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.CaseErasurePurgeTests
open ClaimCore.IntegrationTests.ManagedCopyExternalPublicationFixture

let private ct = CancellationToken.None

let private refuseSubMicrosecondSignedTime () =
    use document =
        JsonDocument.Parse("{\"observedAt\":\"2026-10-01T00:00:00.0000001+00:00\"}")

    Expect.throwsT<InvalidOperationException>
        (fun () ->
            ManagedCopyAdoptionDocumentCommon.instant document.RootElement "observedAt"
            |> ignore)
        "Sub-microsecond signed copy time cannot create unroundtrippable custody evidence."

let private publish owner witness commitments (fixture: PublicationFixture) =
    ManagedCopyExternalPublicationOwner.publish
        owner
        witness
        commitments
        fixture.PrivateLocation
        fixture.Submission
        ct
    |> await

let private openSyntheticCase owner (runtime: Runtime) proposer =
    let actor = runtime.ForActor proposer
    let input = openRequest (Guid.NewGuid()) ("EXT-" + Guid.NewGuid().ToString("N"))
    executeAccepted actor input
    actor, input, caseId owner input.CaseReference

let private requestErasure (actor: IActorClaimsCore) reference =
    let current = review actor reference

    let request =
        change
            (Guid.NewGuid())
            reference
            current
            (LifecycleMutation.RequestErasure "Synthetic external-copy fence")

    match actor.Lifecycle.Apply(request, ct) |> await with
    | LifecycleWriteOutcome.Applied _ -> ()
    | _ -> failtest "Synthetic erasure request failed."

let private publication owner _ (witness: WitnessProtocol) (runtime: Runtime) proposer _ _ _ _ =
    let actor, input, caseId = openSyntheticCase owner runtime proposer
    let first = create owner witness runtime proposer caseId
    let later = create owner witness runtime proposer caseId
    let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity

    let sequence =
        match publish owner witness commitments first with
        | ExternalCopyPublicationOutcome.Published(id, sequence) when
            id = first.Submission.PublicationId && sequence > 0L
            ->
            sequence
        | _ -> failtest "Signed external publication failed."

    Expect.equal
        (publish owner witness commitments first)
        (ExternalCopyPublicationOutcome.Published(first.Submission.PublicationId, sequence))
        "Exact retry returns the original witnessed receipt"

    use connection = new NpgsqlConnection(owner)
    connection.Open()

    DataAudit.runWithSuppression connection witness (Some commitments) ct
    |> await
    |> ignore

    requestErasure actor input.CaseReference

    match publish owner witness commitments later with
    | ExternalCopyPublicationOutcome.ResourceUnavailable -> ()
    | _ -> failtest "Post-fence publication was not refused."

    use count =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.managed_copy_external_publications "
            + "WHERE case_id=@case",
            connection
        )

    Sql.uuid count "case" caseId
    Expect.equal (count.ExecuteScalar() :?> int64) 1L "Only the pre-fence copy was published"

let private refusals owner _ (witness: WitnessProtocol) (runtime: Runtime) proposer _ _ _ _ =
    refuseSubMicrosecondSignedTime ()
    let _, _, caseId = openSyntheticCase owner runtime proposer
    let fixture = create owner witness runtime proposer caseId
    let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity
    let original = File.ReadAllBytes(fixture.CiphertextPath)
    File.WriteAllBytes(fixture.CiphertextPath, RandomNumberGenerator.GetBytes(original.Length))

    Expect.equal
        (publish owner witness commitments fixture)
        ExternalCopyPublicationOutcome.PrivateLocationUnknown
        "Changed actual bytes cannot be published from matching signed metadata"

    File.WriteAllBytes(fixture.CiphertextPath, original)
    let damaged = Array.copy fixture.Submission.Inspection.Signature
    damaged[0] <- damaged[0] ^^^ 1uy

    let invalid =
        { fixture with
            Submission =
                { fixture.Submission with
                    Inspection =
                        { fixture.Submission.Inspection with
                            Signature = damaged
                        }
                }
        }

    Expect.equal
        (publish owner witness commitments invalid)
        ExternalCopyPublicationOutcome.ResourceUnavailable
        "Changed independent inspector signature is refused before witness intent"

    match publish owner witness commitments fixture with
    | ExternalCopyPublicationOutcome.Published _ -> ()
    | _ -> failtest "Valid publication after refusals failed."

    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use change =
        new NpgsqlCommand(
            "UPDATE claimcore.managed_copy_external_publications "
            + "SET inspection_signature=set_byte(inspection_signature,0,"
            + "get_byte(inspection_signature,0) # 1) WHERE publication_id=@publication",
            connection
        )

    Sql.uuid change "publication" fixture.Submission.PublicationId
    Expect.equal (change.ExecuteNonQuery()) 1 "One synthetic signature was changed"

    Expect.throws
        (fun () ->
            DataAudit.runWithSuppression connection witness (Some commitments) ct
            |> await
            |> ignore)
        "Full audit rejects changed signed publication evidence"

let tests =
    testList
        "external copy publication"
        [
            testCase
                "[CC-ERASE-001] signed PRESENT publication replays exactly and erasure fences new copies"
                (fun _ -> setup publication)
            testCase
                "[CC-AUDIT-001] external publication verifies actual bytes and detects signature tampering"
                (fun _ -> setup refusals)
        ]
