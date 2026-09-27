module ClaimCore.IntegrationTests.ManagedCopyAdoptionApprovalTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport

let private ct = CancellationToken.None

let private exportRow owner caseId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT c.copy_id,c.ciphertext_sha256,c.ciphertext_bytes,c.captured_at,"
            + "c.retain_until,e.witness_sequence,e.witness_entry_hash "
            + "FROM claimcore.managed_copies c JOIN claimcore.recovery_artifact_exports e "
            + "ON e.export_id=c.product_export_id WHERE c.source_case_id=@case "
            + "AND c.producer_kind='PRODUCT_EXPORT'",
            connection
        )

    Sql.uuid command "case" caseId
    use reader = command.ExecuteReader()
    Expect.isTrue (reader.Read()) "Synthetic product export copy is present"

    let value =
        reader.GetGuid(0),
        reader.GetFieldValue<byte array>(1),
        reader.GetInt64(2),
        reader.GetFieldValue<DateTimeOffset>(3),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.GetInt64(5),
        reader.GetFieldValue<byte array>(6)

    Expect.isFalse (reader.Read()) "One product export copy is expected"
    value

let private request
    (copyId: Guid)
    (caseId: Guid)
    (sha: byte array)
    (bytes: int64)
    (captured: DateTimeOffset)
    (retained: DateTimeOffset)
    (sequence: int64)
    (entryHash: byte array)
    (custodian: Guid)
    (registry: Guid)
    (inspector: Guid)
    =
    ({
        ApprovalId = Guid.NewGuid()
        AdoptionEventId = Guid.NewGuid()
        CopyId = copyId
        CaseId = caseId
        Origin = CopyAdoptionOrigin.ProductExport(copyId, sequence, entryHash)
        CiphertextSha256 = sha
        CiphertextBytes = bytes
        CapturedAt = captured
        RetainUntil = retained
        LocationCommitment = RandomNumberGenerator.GetBytes(32)
        CustodianCommitment = RandomNumberGenerator.GetBytes(32)
        CustodianSigningKeyId = custodian
        RegistrySigningKeyId = registry
        InspectorSigningKeyId = inspector
        CustodianCanonicalSha256 = RandomNumberGenerator.GetBytes(32)
        RegistryCanonicalSha256 = RandomNumberGenerator.GetBytes(32)
        InspectionReportSha256 = RandomNumberGenerator.GetBytes(32)
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15.0)
    }
    : CopyAdoptionApprovalRequest)

let private livePurge owner (witness: WitnessProtocol) change caseId =
    let draft = CaseLifecycleCandidate.draft caseId change
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
    | OwnerPurgeOutcome.Purged _ -> ()
    | _ -> failtest "Product export live purge prerequisite failed."

let private signerIds owner (witness: WitnessProtocol) runtime proposer =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let custodian = human "adoption-custodian"
    let inspector = human "adoption-inspector"
    grantCustodian runtime proposer custodian
    grantCustodian runtime proposer inspector

    let copyKey, _, copyKeyId, _ =
        registeredSigner
            runtime
            proposer
            custodian
            CopySignerPurpose.CopyAttestor
            witness
            connection

    let registryKey, _, registryKeyId, _ =
        registeredSigner
            runtime
            proposer
            custodian
            CopySignerPurpose.LocationRegistry
            witness
            connection

    let inspectorKey, _, inspectorKeyId, _ =
        registeredSigner
            runtime
            proposer
            inspector
            CopySignerPurpose.LocationInspector
            witness
            connection

    use _copyKey = copyKey
    use _registryKey = registryKey
    use _inspectorKey = inspectorKey
    copyKeyId, registryKeyId, inspectorKeyId

let private prepare owner (witness: WitnessProtocol) runtime proposer change caseId =
    livePurge owner witness change caseId

    let copyKeyId, registryKeyId, inspectorKeyId =
        signerIds owner witness runtime proposer

    let copyId, sha, bytes, captured, retained, sequence, entryHash =
        exportRow owner caseId

    request
        copyId
        caseId
        sha
        bytes
        captured
        retained
        sequence
        entryHash
        copyKeyId
        registryKeyId
        inspectorKeyId

let private assertRefusals
    (runtime: Runtime)
    proposer
    first
    (witness: WitnessProtocol)
    (draft: CopyAdoptionApprovalRequest)
    =
    let actor = runtime.ForActor proposer
    let before = witness.Snapshot().TipSequence

    let wrong =
        { draft with
            CiphertextSha256 = Array.create 32 0x11uy
        }

    Expect.equal
        (actor.ApproveCopyAdoption(wrong, ct) |> await)
        CopyAdoptionApprovalOutcome.ResourceUnavailable
        "Changed source bytes cannot gain owner approval"

    Expect.equal (witness.Snapshot().TipSequence) before "Definite source refusal wrote no intent"

    Expect.equal
        ((runtime.ForActor first).ApproveCopyAdoption(draft, ct) |> await)
        CopyAdoptionApprovalOutcome.ResourceUnavailable
        "DATA_STEWARD without OWNER role cannot approve custody adoption"

let private assertApproved owner (runtime: Runtime) proposer (draft: CopyAdoptionApprovalRequest) =
    let actor = runtime.ForActor proposer

    match actor.ApproveCopyAdoption(draft, ct) |> await with
    | CopyAdoptionApprovalOutcome.Approved(id, revision) when id = draft.ApprovalId && revision > 0L ->
        ()
    | _ -> failtest "Actor-bound product export adoption approval failed."

    match actor.ApproveCopyAdoption(draft, ct) |> await with
    | CopyAdoptionApprovalOutcome.Approved(id, _) when id = draft.ApprovalId -> ()
    | _ -> failtest "Exact adoption approval retry diverged."

    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use check =
        new NpgsqlCommand(
            "SELECT state,revision FROM claimcore.managed_copies WHERE copy_id=@copy",
            connection
        )

    Sql.uuid check "copy" draft.CopyId
    use reader = check.ExecuteReader()
    Expect.isTrue (reader.Read()) "Product export copy is retained"

    Expect.equal
        (reader.GetString(0), reader.GetInt64(1))
        ("UNKNOWN", 1L)
        "Owner draft approval alone cannot claim custody or absence"

let private auditApproved owner (witness: WitnessProtocol) approvalId =
    use auditConnection = new NpgsqlConnection(owner)
    auditConnection.Open()

    let summary =
        DataAudit.runWithSuppression
            auditConnection
            witness
            (Some(FixturePrivateFiles.syntheticCommitments witness.Identity))
            ct
        |> await

    Expect.equal summary.ErasureFences 1L "Unused adoption draft retains full audit"

    use mutation = new NpgsqlConnection(owner)
    mutation.Open()

    use tamper =
        new NpgsqlCommand(
            "UPDATE claimcore.managed_copy_adoption_approvals "
            + "SET approved_at=approved_at+interval '1 second' WHERE approval_id=@approval",
            mutation
        )

    Sql.uuid tamper "approval" approvalId
    Expect.equal (tamper.ExecuteNonQuery()) 1 "Synthetic approval clock was changed"

    Expect.throws
        (fun () ->
            DataAudit.runWithSuppression
                auditConnection
                witness
                (Some(FixturePrivateFiles.syntheticCommitments witness.Identity))
                ct
            |> await
            |> ignore)
        "Changed DB-clock approval instant must quarantine full audit"

let private run
    owner
    _writer
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    first
    _second
    _
    _
    (change: LifecycleChange)
    _
    caseId
    =
    let draft = prepare owner witness runtime proposer change caseId
    assertRefusals runtime proposer first witness draft
    assertApproved owner runtime proposer draft
    auditApproved owner witness draft.ApprovalId

let tests =
    testList
        "copy adoption owner approval"
        [
            testCase
                "[CC-ERASE-001] product export adoption approval and tamper audit remain pending"
                (fun _ -> CaseErasureArtifactTests.withArtifact run)
        ]
