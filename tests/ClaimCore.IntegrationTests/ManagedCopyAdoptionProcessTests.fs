module ClaimCore.IntegrationTests.ManagedCopyAdoptionProcessTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyAdoptionFixture
open ClaimCore.IntegrationTests.DatabaseVerifyDataTests

let private ct = CancellationToken.None

let private proposal (submission: CopyAdoptionSubmission) =
    JsonSerializer.SerializeToUtf8Bytes(
        {|
            version = 1
            approvalId = submission.ApprovalId.ToString("D")
            adoptionEventId = submission.AdoptionEventId.ToString("D")
            custodianCanonicalBase64 = Convert.ToBase64String(submission.Custodian.Canonical)
            custodianSignatureBase64 = Convert.ToBase64String(submission.Custodian.Signature)
            registryCanonicalBase64 = Convert.ToBase64String(submission.Registry.Canonical)
            registrySignatureBase64 = Convert.ToBase64String(submission.Registry.Signature)
            inspectionCanonicalBase64 = Convert.ToBase64String(submission.Inspection.Canonical)
            inspectionSignatureBase64 = Convert.ToBase64String(submission.Inspection.Signature)
        |}
    )

let private mapping (copyId: Guid) (caseId: Guid) custodianId location =
    JsonSerializer.SerializeToUtf8Bytes(
        {|
            version = 1
            copyId = copyId.ToString("D")
            caseId = caseId.ToString("D")
            custodianId = custodianId
            location = location
        |}
    )

let private inputs baseFiles (value: AdoptionFixture) mapPath =
    let capability =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Synthetic writer capability is absent")

    baseFiles
    @ [
        ("CLAIMCORE_WRITER_CAPABILITY_FILE", capability)
        ("CLAIMCORE_COPY_LOCATION_MAPPING_FILE", mapPath)
        ("CLAIMCORE_COPY_COMMITMENT_KEY_FILE", value.CommitmentKeyPath)
    ]

let private invoke files path (value: AdoptionFixture) =
    let code, response = runCommand "adopt-managed-copy" [ path ] files
    use response = response
    let root = response.RootElement
    let safe = root.GetRawText()

    Expect.isFalse
        (safe.Contains(path, StringComparison.Ordinal))
        "Private proposal path is not emitted"

    Expect.isFalse
        (safe.Contains(value.CiphertextPath, StringComparison.Ordinal))
        "Ciphertext path is not emitted"

    Expect.isFalse
        (safe.Contains(value.CustodianId, StringComparison.Ordinal))
        "Custodian is not emitted"

    let command =
        match root.TryGetProperty("command") with
        | true, token -> token.GetString()
        | false, _ -> "NONE"

    code, root.GetProperty("operationOutcome").GetString(), command

let private assertNoAdoption owner (witness: WitnessProtocol) eventId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.managed_copy_adoptions WHERE adoption_event_id=@event",
            connection
        )

    Sql.uuid command "event" eventId
    Expect.equal (command.ExecuteScalar() :?> int64) 0L "Refused input wrote no adoption receipt"

    use useCheck =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.managed_copy_adoption_approval_uses "
            + "WHERE adoption_event_id=@event",
            connection
        )

    Sql.uuid useCheck "event" eventId
    Expect.equal (useCheck.ExecuteScalar() :?> int64) 0L "Refused input did not consume an approval"

    Expect.isNone
        (witness.EvidenceStore.TryReadEvidence(eventId, Intent))
        "Refused input did not reserve witness intent"

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
    | _ -> failtest "Synthetic product export prerequisite failed."

let private refused owner (witness: WitnessProtocol) (value: AdoptionFixture) path files expected =
    let code, outcome, _ = invoke files path value
    Expect.isGreaterThan code 0 "Unsafe private adoption input is refused"
    Expect.equal outcome expected "Refusal has the correct pre-intent knowledge"
    assertNoAdoption owner witness value.Submission.AdoptionEventId

let private badProposals directory owner witness value proposalPath configured =
    refused
        owner
        witness
        value
        (Path.Combine(directory, "missing-proposal.json"))
        configured
        "NOT_STARTED"

    let malformed =
        privateBytes directory "malformed-proposal.json" (Encoding.UTF8.GetBytes("{"))

    refused owner witness value malformed configured "NOT_STARTED"
    let source = Encoding.UTF8.GetString(proposal value.Submission)

    let duplicate =
        privateBytes
            directory
            "duplicate-proposal.json"
            (Encoding.UTF8.GetBytes("{\"version\":1," + source.Substring(1)))

    refused owner witness value duplicate configured "NOT_STARTED"

    let linked = Path.Combine(directory, "linked-proposal.json")
    File.CreateSymbolicLink(linked, proposalPath) |> ignore
    refused owner witness value linked configured "NOT_STARTED"

let private wrongWriterCapability directory owner witness value proposalPath configured =
    let path =
        privateBytes
            directory
            "wrong-writer.cap"
            (Security.Cryptography.RandomNumberGenerator.GetBytes(32))

    let wrong =
        configured
        |> List.map (fun (name, current) ->
            if name = "CLAIMCORE_WRITER_CAPABILITY_FILE" then
                name, path
            else
                name, current)

    refused owner witness value proposalPath wrong "NOT_STARTED"

let private refusedMappings
    directory
    owner
    (witness: WitnessProtocol)
    (value: AdoptionFixture)
    caseId
    (artifact: byte array)
    proposalPath
    baseFiles
    configured
    =
    let wrongMap =
        privateBytes
            directory
            "wrong-mapping.json"
            (mapping value.Request.CopyId caseId "wrong-custodian" value.CiphertextPath)

    refused owner witness value proposalPath (inputs baseFiles value wrongMap) "NOT_COMMITTED"

    let relativeMap =
        privateBytes
            directory
            "relative-mapping.json"
            (mapping value.Request.CopyId caseId value.CustodianId "ciphertext.bin")

    refused owner witness value proposalPath (inputs baseFiles value relativeMap) "NOT_COMMITTED"

    let wrongBytes =
        privateBytes directory "wrong-ciphertext.bin" (Encoding.UTF8.GetBytes("different"))

    let wrongObjectMap =
        privateBytes
            directory
            "wrong-object-mapping.json"
            (mapping value.Request.CopyId caseId value.CustodianId wrongBytes)

    refused owner witness value proposalPath (inputs baseFiles value wrongObjectMap) "NOT_COMMITTED"

    try
        File.WriteAllBytes(value.CiphertextPath, [| 0uy |])
        refused owner witness value proposalPath configured "NOT_COMMITTED"
    finally
        File.WriteAllBytes(value.CiphertextPath, artifact)

let private run
    owner
    writer
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
    let value = fixtureDetails owner witness runtime proposer caseId artifact
    use _transitionKey = value.TransitionSigningKey

    match (runtime.ForActor proposer).ApproveCopyAdoption(value.Request, ct) |> await with
    | CopyAdoptionApprovalOutcome.Approved(id, _) when id = value.Request.ApprovalId -> ()
    | _ -> failtest "Synthetic adoption approval failed."

    let directory = privateRoot ()

    let proposalPath =
        privateBytes directory "adoption-proposal.json" (proposal value.Submission)

    let mapPath =
        privateBytes
            directory
            "adoption-mapping.json"
            (mapping value.Request.CopyId caseId value.CustodianId value.CiphertextPath)

    let baseFiles = files directory owner writer witness
    let configured = inputs baseFiles value mapPath

    badProposals directory owner witness value proposalPath configured
    wrongWriterCapability directory owner witness value proposalPath configured
    refusedMappings directory owner witness value caseId artifact proposalPath baseFiles configured

    let code, outcome, command = invoke configured proposalPath value

    Expect.equal
        (code, outcome, command)
        (0, "COMPLETED", "ADOPT_MANAGED_COPY")
        "Owner process confirms exact signed adoption"

    let retryCode, retryOutcome, _ = invoke configured proposalPath value
    Expect.equal (retryCode, retryOutcome) (0, "COMPLETED") "Exact private retry is idempotent"

    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let summary =
        DataAudit.runWithSuppression connection witness (Some commitments) ct |> await

    Expect.equal summary.ManagedExports 1L "Original export remains fully audited"
    Expect.equal summary.ErasureFences 1L "Adopted copy remains a pending erasure liability"

let tests =
    testList
        "owner managed-copy adoption process"
        [
            testCase
                "[CC-ERASE-001] owner process reads exact private signed adoption evidence"
                (fun _ -> CaseErasureArtifactTests.withArtifact run)
        ]
