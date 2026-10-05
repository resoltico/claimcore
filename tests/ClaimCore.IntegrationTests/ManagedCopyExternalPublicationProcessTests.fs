module ClaimCore.IntegrationTests.ManagedCopyExternalPublicationProcessTests

open System
open System.IO
open System.Security.Cryptography
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
open ClaimCore.IntegrationTests.DatabaseVerifyDataTests
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyExternalPublicationFixture
open ClaimCore.IntegrationTests.CaseLifecycleStoreFixture
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.CaseErasurePurgeTests

let private proposal (submission: ExternalCopyPublicationSubmission) =
    JsonSerializer.SerializeToUtf8Bytes(
        {|
            version = 1
            publicationId = submission.PublicationId.ToString("D")
            registryCanonicalBase64 = Convert.ToBase64String(submission.Registry.Canonical)
            registrySignatureBase64 = Convert.ToBase64String(submission.Registry.Signature)
            inspectionCanonicalBase64 = Convert.ToBase64String(submission.Inspection.Canonical)
            inspectionSignatureBase64 = Convert.ToBase64String(submission.Inspection.Signature)
        |}
    )

let private mapping (fixture: PublicationFixture) custodian location =
    JsonSerializer.SerializeToUtf8Bytes(
        {|
            version = 1
            copyId = fixture.CopyId.ToString("D")
            caseId = fixture.CaseId.ToString("D")
            custodianId = custodian
            location = location
        |}
    )

let private inputs
    directory
    owner
    writer
    (witness: WitnessProtocol)
    (fixture: PublicationFixture)
    mapPath
    =
    let capability =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Scoped synthetic writer capability is absent")

    files directory owner writer witness
    @ [
        "CLAIMCORE_WRITER_CAPABILITY_FILE", capability
        "CLAIMCORE_COPY_LOCATION_MAPPING_FILE", mapPath
        "CLAIMCORE_COPY_COMMITMENT_KEY_FILE", fixture.CommitmentKeyPath
    ]

let private invoke files path (fixture: PublicationFixture) =
    let code, response = runCommand "publish-external-copy" [ path ] files
    use response = response
    let root = response.RootElement
    let safe = root.GetRawText()
    Expect.isFalse (safe.Contains(path, StringComparison.Ordinal)) "Proposal path stays private"

    Expect.isFalse
        (safe.Contains(fixture.CiphertextPath, StringComparison.Ordinal))
        "Ciphertext path stays private"

    Expect.isFalse
        (safe.Contains(fixture.CustodianId, StringComparison.Ordinal))
        "Custodian stays private"

    let command =
        match root.TryGetProperty("command") with
        | true, value -> value.GetString()
        | false, _ -> "NONE"

    code, root.GetProperty("operationOutcome").GetString(), command

let private noReceipt owner (witness: WitnessProtocol) publicationId =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT count(*) FROM claimcore.managed_copy_external_publications "
            + "WHERE publication_id=@publication",
            connection
        )

    Sql.uuid command "publication" publicationId
    Expect.equal (command.ExecuteScalar() :?> int64) 0L "Refusal wrote no publication receipt"

    Expect.isNone
        ((witness.EvidenceStore
            .TryReadEvidence(publicationId, Intent, CancellationToken.None)
            .GetAwaiter()
            .GetResult()))
        "Refusal reserved no witness intent"

let private refusals
    directory
    owner
    witness
    (fixture: PublicationFixture)
    (original: byte array)
    proposalPath
    configured
    =
    let refuse path files expected =
        let code, outcome, _ = invoke files path fixture
        Expect.isGreaterThan code 0 "Unsafe publication input is refused"
        Expect.equal outcome expected "Refusal reports its pre-intent knowledge"
        noReceipt owner witness fixture.Submission.PublicationId

    refuse (Path.Combine(directory, "missing-proposal.json")) configured "NOT_STARTED"

    let malformed =
        privateBytes directory "malformed-proposal.json" (Encoding.UTF8.GetBytes("{"))

    refuse malformed configured "NOT_STARTED"
    let source = Encoding.UTF8.GetString(proposal fixture.Submission)

    let duplicate =
        privateBytes
            directory
            "duplicate-proposal.json"
            (Encoding.UTF8.GetBytes("{\"version\":1," + source.Substring(1)))

    refuse duplicate configured "NOT_STARTED"
    let linked = Path.Combine(directory, "linked-proposal.json")
    File.CreateSymbolicLink(linked, proposalPath) |> ignore
    refuse linked configured "NOT_STARTED"

    let wrongCustodian =
        privateBytes
            directory
            "wrong-custodian.json"
            (mapping fixture "wrong-custodian" fixture.CiphertextPath)

    let remapped files mapPath =
        files
        |> List.map (fun (name, path) ->
            if name = "CLAIMCORE_COPY_LOCATION_MAPPING_FILE" then
                name, mapPath
            else
                name, path)

    refuse proposalPath (remapped configured wrongCustodian) "NOT_COMMITTED"

    let relative =
        privateBytes
            directory
            "relative-location.json"
            (mapping fixture fixture.CustodianId "external-copy.bin")

    refuse proposalPath (remapped configured relative) "NOT_COMMITTED"

    try
        File.WriteAllBytes(fixture.CiphertextPath, [| 0uy |])
        refuse proposalPath configured "NOT_COMMITTED"
    finally
        File.WriteAllBytes(fixture.CiphertextPath, original)

    let wrongCapability =
        privateBytes directory "wrong-writer.cap" (RandomNumberGenerator.GetBytes(32))

    let wrongFiles =
        configured
        |> List.map (fun (name, path) ->
            if name = "CLAIMCORE_WRITER_CAPABILITY_FILE" then
                name, wrongCapability
            else
                name, path)

    refuse proposalPath wrongFiles "NOT_STARTED"

let private run owner _ (witness: WitnessProtocol) (runtime: Runtime) proposer _ _ _ writer =
    let actor = runtime.ForActor proposer

    let input =
        openRequest (Guid.NewGuid()) ("EXT-PROCESS-" + Guid.NewGuid().ToString("N"))

    executeAccepted actor input
    let caseId = caseId owner input.CaseReference
    let fixture = create owner witness runtime proposer caseId
    let original = File.ReadAllBytes(fixture.CiphertextPath)
    let directory = privateRoot ()

    let proposalPath =
        privateBytes directory "publication-proposal.json" (proposal fixture.Submission)

    let mapPath =
        privateBytes
            directory
            "copy-location.json"
            (mapping fixture fixture.CustodianId fixture.CiphertextPath)

    let configured = inputs directory owner writer witness fixture mapPath
    refusals directory owner witness fixture original proposalPath configured

    let code, outcome, command = invoke configured proposalPath fixture

    Expect.equal
        (code, outcome, command)
        (0, "COMPLETED", "PUBLISH_EXTERNAL_COPY")
        "Owner process confirms one exact pre-fence publication"

    let retryCode, retryOutcome, _ = invoke configured proposalPath fixture

    Expect.equal
        (retryCode, retryOutcome)
        (0, "COMPLETED")
        "Exact private retry returns the original publication"

    use connection = new NpgsqlConnection(owner)
    connection.Open()
    let commitments = FixturePrivateFiles.syntheticCommitments witness.Identity

    DataAudit.runWithSuppression connection witness (Some commitments) CancellationToken.None
    |> await
    |> ignore

let tests =
    testList
        "external copy publication owner process"
        [
            testCase
                "[CC-ERASE-001] owner process publishes one signed pre-fence external copy"
                (fun _ -> setup run)
        ]
