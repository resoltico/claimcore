module internal ClaimCore.IntegrationTests.RestoreProduceInventoryDigestChecks

open System
open System.IO
open System.Text
open Expecto
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.IntegrationTests.RestoreProduceSourceEvidence

let private changedDigest (report: RestoreReportClaims) =
    if report.SignedInventoryFileSha256 = String('a', 64) then
        String('b', 64)
    else
        String('a', 64)

let private changedSignedFile
    (input: RestoreProduceInput)
    (facts: RestoredPairFacts)
    (checkpointKey: Key)
    report
    otherDigest
    verify
    =
    let file = input.Index.InventorySnapshotFile
    let signature = input.Index.InventorySnapshotSignatureFile
    let original = File.ReadAllBytes(file)
    let originalSignature = File.ReadAllBytes(signature)
    let source = Encoding.ASCII.GetString(original)
    let needle = "\"snapshotSha256\":\"" + facts.ManagedCopySnapshotSha256 + "\""

    if not (source.Contains(needle, StringComparison.Ordinal)) then
        failtest "Signed inventory lacks the live managed-copy snapshot digest"

    let replacement = "\"snapshotSha256\":\"" + otherDigest + "\""
    let changed = source.Replace(needle, replacement, StringComparison.Ordinal)
    let changedBytes = Encoding.ASCII.GetBytes(changed)

    try
        File.WriteAllBytes(file, changedBytes)
        File.WriteAllBytes(signature, SignatureAlgorithm.Ed25519.Sign(checkpointKey, changedBytes))

        let changedFileHash = sha changedBytes

        Expect.throws
            (fun () ->
                verify
                    { report with
                        SignedInventoryFileSha256 = changedFileHash
                    })
            "A correctly signed file with a changed row-set digest refuses"
    finally
        File.WriteAllBytes(file, original)
        File.WriteAllBytes(signature, originalSignature)

let verifyInventoryDigests
    (input: RestoreProduceInput)
    (produced: SignedRestoreProduction)
    (facts: RestoredPairFacts)
    (checkpointKey: Key)
    =
    let report =
        DatabaseRestoreReportClaims.parse produced.Evidence.Report DateTimeOffset.UtcNow
        |> Option.defaultWith (fun () -> failtest "Produced signed report is invalid")

    let publicKey = checkpointKey.PublicKey.Export(KeyBlobFormat.RawPublicKey)

    let verify claims =
        DatabaseRestoreArtifacts.verifyInventory input.Index claims publicKey facts

    verify report
    let otherDigest = changedDigest report

    Expect.throws
        (fun () ->
            verify
                { report with
                    SignedInventoryFileSha256 = otherDigest
                })
        "A changed signed inventory file digest refuses"

    changedSignedFile input facts checkpointKey report otherDigest verify
