module internal ClaimCore.IntegrationTests.DatabaseWitnessPruneProcessInputs

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryDocuments

let private ct = CancellationToken.None

let private ownerWitness (writer: string) =
    let builder = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    builder.Database <- NpgsqlConnectionStringBuilder(writer).Database
    builder.ConnectionString

let proposal (review: TombstoneReview) =
    let expiry = DateTimeOffset.UtcNow.AddHours(1.0)

    {
        EventId = Guid.NewGuid()
        CaseId = review.CaseId
        PurgeEventId = review.PurgeEventId
        PurgeWitnessSequence = review.PurgeWitnessSequence
        PurgeWitnessEpoch = review.PurgeWitnessEpoch
        PurgeWitnessHash = review.PurgeWitnessHash
        CutoffSequence = review.CutoffSequence
        CutoffHash = review.CutoffHash
        TargetCount = review.TargetCount
        TargetDigest = review.TargetDigest
        ExpectedAuthorityRevision = review.AuthorityRevision
        ExpectedAuthorityHash = review.AuthorityHash
        ValidUntil = DateTimeOffset(expiry.UtcTicks - expiry.UtcTicks % 10L, TimeSpan.Zero)
    }

let inputFiles directory owner writer witness registry inspection copyKey =
    let baseFiles = DatabaseVerifyDataTests.files directory owner writer witness

    let ownerPath =
        privateBytes
            directory
            "witness-owner.connection"
            (Text.Encoding.UTF8.GetBytes(ownerWitness writer))

    let capabilityPath =
        Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE")
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () ->
            failtest "The isolated witness writer capability path is required.")

    baseFiles
    @ [
        "CLAIMCORE_WITNESS_ADMIN_CONNECTION_FILE", ownerPath
        "CLAIMCORE_WRITER_CAPABILITY_FILE", capabilityPath
        "CLAIMCORE_COPY_LOCATION_REGISTRY_FILE", registry
        "CLAIMCORE_COPY_LOCATION_INSPECTION_FILE", inspection
        "CLAIMCORE_COPY_COMMITMENT_KEY_FILE", copyKey
    ]

let invokeOwner files proposalPath =
    let code, document =
        DatabaseVerifyDataTests.runCommand "prune-witness-payload" [ proposalPath ] files

    use result = document
    let body = result.RootElement.GetRawText()
    Expect.isFalse (body.Contains(proposalPath, StringComparison.Ordinal)) "Private path omitted"

    Expect.isFalse
        (body.Contains("Synthetic live purge reason", StringComparison.Ordinal))
        "Reason omitted"

    Expect.isFalse (body.Contains("PURGE-", StringComparison.Ordinal)) "Reference omitted"

    let diagnostic =
        match result.RootElement.TryGetProperty("diagnostic") with
        | true, value -> value.GetProperty("id").GetString()
        | false, _ -> "NONE"

    code, result.RootElement.GetProperty("operationOutcome").GetString(), diagnostic

let wrongCapabilityRefused
    directory
    owner
    (witness: WitnessProtocol)
    files
    proposalPath
    (request: TombstonePruneProposal)
    =
    let wrong = RandomNumberGenerator.GetBytes(32)

    try
        let path = privateBytes directory "wrong-writer.cap" wrong

        let wrongFiles =
            files
            |> List.map (fun (name, value) ->
                if name = "CLAIMCORE_WRITER_CAPABILITY_FILE" then
                    name, path
                else
                    name, value)

        let code, outcome, _ = invokeOwner wrongFiles proposalPath
        Expect.isTrue (code <> 0) "Wrong writer capability refuses the owner command"
        Expect.notEqual outcome "COMPLETED" "Wrong writer capability cannot report completion"
        use connection = new NpgsqlConnection(owner)
        connection.Open()

        use receipt =
            new NpgsqlCommand(
                "SELECT witness_prune_event_id FROM claimcore.case_erasure_tombstones WHERE case_id=@case",
                connection
            )

        Sql.uuid receipt "case" request.CaseId

        Expect.isTrue
            (Convert.IsDBNull(receipt.ExecuteScalar()))
            "No primary prune receipt was written"

        Expect.isNone
            (witness.EvidenceStore.TryReadEvidence(request.EventId, Intent))
            "No witness prune intent was written"
    finally
        CryptographicOperations.ZeroMemory(wrong)

let rejectedProposal files path =
    let code, outcome, _ = invokeOwner files path
    Expect.isTrue (code <> 0) "Private proposal input is rejected"
    Expect.notEqual outcome "COMPLETED" "Rejected proposal cannot report completion"

let invalidFilesRefused directory files proposalPath =
    rejectedProposal files (IO.Path.Combine(directory, "missing.proposal"))

    let malformed =
        privateBytes directory "malformed.proposal" (Text.Encoding.UTF8.GetBytes("{"))

    rejectedProposal files malformed
    let linked = IO.Path.Combine(directory, "linked.proposal")
    IO.File.CreateSymbolicLink(linked, proposalPath) |> ignore
    rejectedProposal files linked

let preflight
    owner
    (witness: WitnessProtocol)
    (commitments: ISuppressionCommitments)
    (reviewed: TombstoneReview)
    registryPath
    inspectionPath
    copyKeyPath
    =
    withEnvironment registryPath inspectionPath copyKeyPath (fun () ->
        use probe = new NpgsqlConnection(owner)
        probe.Open()

        Expect.isSome
            (seal
                probe
                witness
                reviewed.CaseId
                reviewed.CutoffSequence
                (Convert.FromHexString reviewed.CutoffHash))
            "Signed zero-copy registry independently seals exact known ID set")

    use audit = new NpgsqlConnection(owner)
    audit.Open()

    DataAudit.runWithSuppression audit witness (Some commitments) ct
    |> await
    |> ignore

let private confirmPruned owner (witness: WitnessProtocol) (commitments: ISuppressionCommitments) =
    use audit = new NpgsqlConnection(owner)
    audit.Open()

    let summary =
        DataAudit.runWithSuppression audit witness (Some commitments) ct |> await

    Expect.equal summary.ErasureFences 1L "Process prune remains fully audited"

let completeAndRetry files proposalPath owner witness commitments =
    let firstCode, firstOutcome, firstDiagnostic = invokeOwner files proposalPath
    Expect.equal firstCode 0 ("Owner process exit: " + firstDiagnostic)
    Expect.equal firstOutcome "COMPLETED" "Owner process prunes CASE ciphertext"
    let retryCode, retryOutcome, _ = invokeOwner files proposalPath
    Expect.equal (retryCode, retryOutcome) (0, "COMPLETED") "Exact process retry is idempotent"
    confirmPruned owner witness commitments
