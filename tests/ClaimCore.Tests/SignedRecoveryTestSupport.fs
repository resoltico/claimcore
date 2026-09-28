module ClaimCore.Tests.SignedRecoveryTestSupport

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Tests.Fixtures

let private installation = Guid.Parse("10000000-0000-4000-8000-000000000001")
let private keyId = Guid.Parse("10000000-0000-4000-8000-000000000002")
let internal caseId = Guid.Parse("10000000-0000-4000-8000-000000000003")
let internal preparer = Guid.Parse("10000000-0000-4000-8000-000000000004")
let private importerId = Guid.Parse("10000000-0000-4000-8000-000000000005")
let private now = DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero)
let private encryptionKey = Array.init 32 (fun index -> byte (index + 1))
let private macKey = Array.init 32 (fun index -> byte (index + 41))

let internal importer: ActorBinding =
    {
        Principal =
            PrincipalKey.human "https://issuer.example.test/realms/claimcore" "importer-subject"
            |> Result.defaultWith (fun _ -> invalidOp "Synthetic issuer is invalid.")
        ActorId = importerId
        GrantRevision = 7L
    }

let private artifact canonical operationId =
    {
        KeyId = keyId
        ExportId = Guid.Parse("10000000-0000-4000-8000-000000000006")
        InstallationId = installation
        Epoch = 8L
        CaseId = caseId
        PreparerActorId = preparer
        PreparerGrantRevision = 5L
        ImporterActorId = None
        ExporterActorId = Guid.Parse("10000000-0000-4000-8000-000000000007")
        ExporterGrantRevision = 6L
        OperationId = operationId
        IssuedAt = now.AddMinutes(-1.)
        ExpiresAt = now.AddMinutes(30.)
        CanonicalCommandFormat = RecordVersions.CanonicalCommandFormat
        RequestFingerprintVersion = RecordVersions.RequestFingerprint
        Nonce = Array.init 12 (fun index -> byte (index + 1))
        CanonicalRequest = canonical
    }

let internal authority: IRecoveryArtifactAuthority =
    { new IRecoveryArtifactAuthority with
        member _.Sign(retained, _) =
            Task.FromResult(
                try
                    artifact retained.CanonicalRequest retained.OperationId
                    |> RecoveryEnvelopeV3.encode 65536 encryptionKey macKey
                    |> Ok
                with :? ArgumentException ->
                    Error CoreFault.RetainedCanonicalInvalid
            )

        member _.Verify(source, _) =
            Task.FromResult(
                match
                    RecoveryEnvelopeV3.decode
                        65536
                        (fun candidate ->
                            if candidate = keyId then
                                Some(encryptionKey, macKey)
                            else
                                None)
                        installation
                        8L
                        now
                        (TimeSpan.FromHours(1.))
                        source
                with
                | Error _ -> Error RecoveryRejection.EnvelopeInvalidOrUnsupported
                | Ok verified ->
                    Ok
                        {
                            OperationId = verified.OperationId
                            CaseId = verified.CaseId
                            PreparerActorId = verified.PreparerActorId
                            PreparerGrantRevision = verified.PreparerGrantRevision
                            CanonicalRequest = verified.CanonicalRequest
                        }
            )
    }

let internal verify source =
    authority.Verify(source, CancellationToken.None).Result

let source (request: CommandRequest) =
    let canonical = RequestRecord.encode request

    let bytes =
        RecoveryEnvelopeV3.encode
            65536
            encryptionKey
            macKey
            (artifact canonical request.OperationId)

    let digest = bytes |> SHA256.HashData |> Convert.ToHexStringLower
    bytes, digest
