module ClaimCore.Tests.RecoveryArtifactV3Tests

open System
open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let private request =
    {
        OperationId = Guid.Parse("20000000-0000-4000-8000-000000000901")
        CaseReference = "SIGNED-RECOVERY-001"
        ExpectedVersion = 0L
        Command = Command.Open registration
    }

let private encryptionKey = Array.init 32 (fun index -> byte (index + 1))
let private macKey = Array.init 32 (fun index -> byte (index + 41))
let private now = DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero)
let private installation = Guid.Parse("10000000-0000-4000-8000-000000000001")
let private keyId = Guid.Parse("10000000-0000-4000-8000-000000000002")

let private artifact () =
    {
        KeyId = keyId
        ExportId = Guid.Parse("10000000-0000-4000-8000-000000000006")
        InstallationId = installation
        Epoch = 8L
        CaseId = Guid.Parse("10000000-0000-4000-8000-000000000003")
        PreparerActorId = Guid.Parse("10000000-0000-4000-8000-000000000004")
        PreparerGrantRevision = 5L
        ImporterActorId = Some(Guid.Parse("10000000-0000-4000-8000-000000000005"))
        ExporterActorId = Guid.Parse("10000000-0000-4000-8000-000000000007")
        ExporterGrantRevision = 6L
        OperationId = request.OperationId
        IssuedAt = now.AddMinutes(-1.)
        ExpiresAt = now.AddMinutes(30.)
        CanonicalCommandFormat = RecordVersions.CanonicalCommandFormat
        RequestFingerprintVersion = RecordVersions.RequestFingerprint
        Nonce = Array.init 12 (fun index -> byte (index + 1))
        CanonicalRequest = RequestRecord.encode request
    }

let private encode value =
    RecoveryEnvelopeV3.encode 65536 encryptionKey macKey value

let private decode at epoch source =
    RecoveryEnvelopeV3.decode
        65536
        (fun candidate ->
            if candidate = keyId then
                Some(encryptionKey, macKey)
            else
                None)
        installation
        epoch
        at
        (TimeSpan.FromHours(1.))
        source

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
