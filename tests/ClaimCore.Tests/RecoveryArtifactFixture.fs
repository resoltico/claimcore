module ClaimCore.Tests.RecoveryArtifactFixture

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let request =
    {
        OperationId = Guid.Parse("20000000-0000-4000-8000-000000000901")
        CaseReference = "SIGNED-RECOVERY-001"
        ExpectedVersion = 0L
        Command = Command.Open registration
    }

let encryptionKey = Array.init 32 (fun index -> byte (index + 1))
let macKey = Array.init 32 (fun index -> byte (index + 41))
let now = DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero)
let installation = Guid.Parse("10000000-0000-4000-8000-000000000001")
let keyId = Guid.Parse("10000000-0000-4000-8000-000000000002")

let artifact () =
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

let encode value =
    RecoveryEnvelopeV3.encode 65536 encryptionKey macKey value

let decode at epoch source =
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
