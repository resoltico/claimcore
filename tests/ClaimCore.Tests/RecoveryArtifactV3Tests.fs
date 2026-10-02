module ClaimCore.Tests.RecoveryArtifactV3Tests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

open ClaimCore.Tests.RecoveryArtifactFixture

let private encryptedRoundTrip () =
    let original = artifact ()
    let bytes = encode original
    let text = Encoding.UTF8.GetString(bytes)
    let canonical = Encoding.UTF8.GetString(original.CanonicalRequest)

    Expect.isFalse
        (text.Contains(canonical, StringComparison.Ordinal))
        "Request plaintext is absent"

    Expect.isFalse
        (text.Contains(Convert.ToBase64String(original.CanonicalRequest), StringComparison.Ordinal))
        "Canonical request base64 is absent"

    match decode now original.Epoch bytes with
    | Error reason -> failtestf "Current signed artifact refused: %s" reason
    | Ok restored ->
        Expect.equal restored.CaseId original.CaseId "Reserved case identity survives"
        Expect.equal restored.PreparerActorId original.PreparerActorId "Original preparer survives"
        Expect.equal restored.ExportId original.ExportId "Export event identity survives"
        Expect.equal restored.ExporterActorId original.ExporterActorId "Exporter identity survives"

        Expect.equal
            restored.ExporterGrantRevision
            original.ExporterGrantRevision
            "Exporter grant revision survives"

        Expect.equal
            restored.ImporterActorId
            original.ImporterActorId
            "Importer attribution is bound"

        Expect.equal
            restored.CanonicalRequest
            original.CanonicalRequest
            "Exact request bytes survive"

let private tamperRefusals () =
    let original = artifact ()
    let bytes = encode original
    let text = Encoding.UTF8.GetString(bytes)

    for changed in
        [
            text.Replace(original.CaseId.ToString("D"), Guid.NewGuid().ToString("D"))
            text.Replace(original.ExporterActorId.ToString("D"), Guid.NewGuid().ToString("D"))
            text.Replace(original.ExportId.ToString("D"), Guid.NewGuid().ToString("D"))
            text.Replace("\"epoch\":8", "\"epoch\":9")
            text.Replace("\"macSha256\":", "\"macSha256\":\"00\",\"ignored\":")
            text + " "
            "{\"format\":\"claimcore-recovery\",\"formatVersion\":2}"
        ] do
        Expect.isError
            (decode now 8L (Encoding.UTF8.GetBytes changed))
            "Tampering and v2 are refused"

let private policyRefusals () =
    let original = artifact ()
    let bytes = encode original

    Expect.isError (decode now 9L bytes) "Wrong installation epoch is refused"
    Expect.isError (decode (now.AddHours(1.)) 8L bytes) "Expired artifact is refused"

    let longLived =
        { original with
            ExpiresAt = now.AddHours(2.)
        }
        |> encode

    Expect.isError (decode now 8L longLived) "Configured maximum lifetime is enforced"

    Expect.throws
        (fun () -> encode { original with CaseId = Guid.Empty } |> ignore)
        "A missing case identity cannot be signed"

    Expect.throws
        (fun () ->
            encode
                { original with
                    PreparerActorId = Guid.Empty
                }
            |> ignore)
        "A missing preparer cannot be signed"

    Expect.throws
        (fun () -> RecoveryEnvelopeV3.encode 65536 encryptionKey encryptionKey original |> ignore)
        "Encryption and MAC keys must be distinct"

    Expect.isError
        (RecoveryEnvelopeV3.decode
            65536
            (fun _ -> None)
            installation
            8L
            now
            (TimeSpan.FromHours(1.))
            bytes)
        "Unknown signing key is refused"

    for keys in [ Array.create 32 88uy, macKey; encryptionKey, Array.create 32 89uy ] do
        Expect.isError
            (RecoveryEnvelopeV3.decode
                65536
                (fun _ -> Some keys)
                installation
                8L
                now
                (TimeSpan.FromHours(1.))
                bytes)
            "Wrong encryption or MAC key cannot authenticate the artifact"

[<Tests>]
let tests =
    testList
        "encrypted recovery artifact v3"
        [
            testCase
                "[CC-REC-001] v3 encrypts exact request and binds attribution"
                encryptedRoundTrip
            testCase "[CC-REC-001] v3 rejects tampering and v2" tamperRefusals
            testCase "[CC-REC-001] v3 enforces epoch expiry and key policy" policyRefusals
        ]
