module ClaimCore.IntegrationTests.RecoveryProcessTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.RecordFormat
open ClaimCore.IntegrationTests.Fixtures

let private openRuntime () =
    Runtime.OpenPostgres(appConnection (), CancellationToken.None)
    |> await
    |> Result.defaultWith (fun _ ->
        failtest "Runtime must open for synthetic recovery qualification.")

let private request operationId reference = openRequest operationId reference

let private prepare (core: IClaimsCore) operationId reference =
    match core.Prepare(request operationId reference, CancellationToken.None) |> await with
    | PrepareOutcome.Prepared(details, _) ->
        let digest =
            details.Summary.RequestSha256
            |> Option.defaultWith (fun () ->
                failtest "Prepared recovery identity requires a digest.")

        details, digest
    | _ -> failtest "Expected retained typed preparation."

let private exportedEnvelope (core: IClaimsCore) operationId digest =
    match
        core.Recovery.ExportEnvelope(operationId, digest, CancellationToken.None)
        |> await
    with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found value) -> value
    | _ -> failtest "Expected a public recovery envelope export."

let private validateEnvelope operationId digest (artifact: RecoveryExport) =
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(artifact.Bytes))
    let root = document.RootElement
    let requestBytes = root.GetProperty("canonicalRequestBase64").GetBytesFromBase64()

    Expect.equal
        artifact.FileName
        ($"claimcore-recovery-{operationId:D}.json")
        "Operation-specific filename"

    Expect.equal artifact.MediaType "application/vnd.claimcore.recovery+json" "Recovery media type"
    Expect.equal artifact.RequestSha256 digest "Export digest"
    Expect.equal (root.GetProperty("operationId").GetGuid()) operationId "Operation identity"
    Expect.equal (root.GetProperty("requestSha256").GetString()) digest "Envelope digest"

    Expect.equal
        (requestBytes |> SHA256.HashData |> Convert.ToHexStringLower)
        digest
        "Exact canonical request bytes"

let private assertUnsubmitted (core: IClaimsCore) operationId =
    match
        core.Recovery.Inspect(operationId, None, recoveryPageLimit, CancellationToken.None)
        |> await
    with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RetainedInspection details)) ->
        Expect.equal
            details.Preparation.Summary.State
            PreparationState.Unsubmitted
            "No submission attempt"
    | _ -> failtest "Expected retained preparation inspection."

let private exactExportAndResolution () =
    use runtime = openRuntime ()
    let operationId = Guid.NewGuid()
    let reference = "RECOVERY-" + Guid.NewGuid().ToString("N")
    let _, digest = prepare runtime.Core operationId reference

    match
        runtime.Core.Recovery.ExportEnvelope(
            operationId,
            String.replicate 64 "b",
            CancellationToken.None
        )
        |> await
    with
    | RecoveryQueryOutcome.RecoveryRejected rejection ->
        Expect.equal
            rejection.Code
            RecoveryRejectionCode.RecoveryIdempotencyConflict
            "Digest mismatch is an Application-owned refusal"
    | _ -> failtest "Export must refuse a mismatched digest before yielding recovery bytes."

    let artifact = exportedEnvelope runtime.Core operationId digest
    validateEnvelope operationId digest artifact

    match
        runtime.Core.Recovery.Resolve(operationId, digest, CancellationToken.None)
        |> await
    with
    | ResolveOutcome.ResolveCompleted(_,
                                      _,
                                      DefiniteExecution.Accepted receipt,
                                      SettlementConfirmation.Confirmed) ->
        Expect.equal receipt.OperationId operationId "Accepted exact operation"
        Expect.equal receipt.Snapshot.Fields.CaseReference reference "Exact retained target"
    | _ -> failtest "Expected public exact resolution with confirmed settlement."

let private changedEnvelope mutation expectedCode =
    use runtime = openRuntime ()
    let operationId = Guid.NewGuid()

    let _, digest =
        prepare runtime.Core operationId ("RECOVERY-" + Guid.NewGuid().ToString("N"))

    let original = exportedEnvelope runtime.Core operationId digest
    let changed = mutation original.Bytes

    match
        runtime.Core.Recovery.PreviewEnvelopeImport(changed, CancellationToken.None)
        |> await
    with
    | RecoveryQueryOutcome.RecoveryRejected rejection ->
        Expect.equal rejection.Code expectedCode "Typed import rejection"
    | _ -> failtest "Expected import preview rejection before retention or submission."

    assertUnsubmitted runtime.Core operationId

let private jsonObject (bytes: byte array) =
    match JsonNode.Parse(ReadOnlySpan<byte>(bytes)) with
    | null -> failtest "Synthetic recovery envelope must be JSON."
    | node -> node.AsObject()

let private foreignLineage () =
    changedEnvelope
        (fun bytes ->
            let node = jsonObject bytes
            node["installationId"] <- JsonValue.Create(Guid.NewGuid().ToString("D"))
            Encoding.UTF8.GetBytes(node.ToJsonString()))
        RecoveryRejectionCode.InstallationMismatch

let private incompatibleFingerprint () =
    changedEnvelope
        (fun bytes ->
            let node = jsonObject bytes
            node["requestFingerprintVersion"] <- JsonValue.Create(999)
            Encoding.UTF8.GetBytes(node.ToJsonString()))
        RecoveryRejectionCode.UnsupportedRecoveryArtifact

let private acceptedReceiptCannotBeDismissed () =
    use runtime = openRuntime ()
    let operationId = Guid.NewGuid()
    let reference = "RECOVERY-" + Guid.NewGuid().ToString("N")
    let command = request operationId reference
    let _, digest = prepare runtime.Core operationId reference

    use database = store ()

    Service.executeAsync (database :> IClaimStore) clock command
    |> await
    |> accepted
    |> ignore

    match
        runtime.Core.Recovery.Dismiss(operationId, digest, true, CancellationToken.None)
        |> await
    with
    | RecoveryDismissOutcome.DismissRefused(Some _, rejection) ->
        Expect.equal
            rejection.Code
            RecoveryRejectionCode.RecoveryActionUnavailable
            "Accepted receipt cannot be dismissed even without a submission marker"
    | _ -> failtest "A retained accepted receipt must block dismissal."

    match
        runtime.Core.Recovery.Inspect(operationId, None, recoveryPageLimit, CancellationToken.None)
        |> await
    with
    | RecoveryQueryOutcome.RecoverySucceeded(Lookup.Found(RecoveryInspection.RetainedInspection details)) ->
        Expect.equal details.Preparation.Summary.State PreparationState.Unsubmitted "No marker"

        match details.Observation with
        | Lookup.Found receipt -> Expect.equal receipt.OperationId operationId "Exact receipt"
        | Lookup.NotFound _ -> failtest "Accepted receipt must remain observable."
    | _ -> failtest "Accepted recovery material must remain inspectable."

let tests =
    testList
        "typed retained-request recovery"
        [
            testCase
                "[CC-REC-001] export and resolve preserve the exact retained request"
                exactExportAndResolution
            testCase
                "[CC-REC-001] foreign installation lineage is rejected before submission"
                foreignLineage
            testCase
                "[CC-REC-001] incompatible request fingerprint is rejected before submission"
                incompatibleFingerprint
            testCase
                "[CC-REC-001] accepted receipt blocks dismissal without a lifecycle marker"
                acceptedReceiptCannotBeDismissed
        ]
