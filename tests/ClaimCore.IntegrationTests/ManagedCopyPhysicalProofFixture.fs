module internal ClaimCore.IntegrationTests.ManagedCopyPhysicalProofFixture

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open NSec.Cryptography
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture

let private hex (bytes: byte array) = Convert.ToHexStringLower(bytes)

let private optionalHex =
    function
    | Some(bytes: byte array) -> element (hex bytes)
    | None -> nil

let private optionalText =
    function
    | Some(value: string) -> element value
    | None -> nil

let holder (connection: NpgsqlConnection) keyId =
    use command =
        new NpgsqlCommand(
            "SELECT holder_actor_id FROM claimcore.managed_copy_signers WHERE signing_key_id=@key",
            connection
        )

    Sql.uuid command "key" keyId
    command.ExecuteScalar() :?> Guid

let private recoveredFields (copy: ManagedCopyAttestation) (tip: Snapshot) =
    [
        "decryptedSha256", element (hex (SHA256.HashData([| 7uy |])))
        "decryptedBytes", element 128L
        "pgVerifyBackupManifestSha256", optionalHex copy.BackupManifestSha256
        "recoveredPostgresSystemId", element copy.PostgresSystemId.Value
        "recoveredTimeline", element copy.Timeline.Value
        "recoveredInstallationId", element (tip.Identity.InstallationId.ToString("D"))
        "recoveredLineageId", element (tip.Identity.LineageId.ToString("D"))
        "recoveredEpoch", element tip.Identity.Epoch
        "recoveredRowCount", element 0L
        "walFirstRecordParsed", element false
        "walTimelineMatched", element false
        "pgVerifyBackup", element true
        "isolatedBoot", element true
        "recoveredIdentityChecked", element true
        "ciphertextRehashed", element true
        "decrypted", element true
    ]

let private proofTime () =
    let checkedAt =
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds())

    checkedAt, checkedAt.AddMinutes(4.)

let private signerFields
    (verifierKeyId: Guid)
    (verifierHolder: Guid)
    (checkedAt: DateTimeOffset)
    (validUntil: DateTimeOffset)
    =
    [
        "verifierSigningKeyId", element (verifierKeyId.ToString("D"))
        "verifierHolderActorId", element (verifierHolder.ToString("D"))
        "checkedAt", element (stamp checkedAt)
        "validUntil", element (stamp validUntil)
        "fullPairReady", element false
        "realDataReady", element false
    ]

let signedBaseProof
    (copy: ManagedCopyAttestation)
    (tip: Snapshot)
    (eventId: Guid)
    (revision: int64)
    (verifierKeyId: Guid)
    (verifierHolder: Guid)
    (key: Key)
    (algorithm: SignatureAlgorithm)
    =
    let checkedAt, validUntil = proofTime ()

    let canonical =
        canonical (
            [
                "format", element "claimcore-managed-copy-physical-verification-1"
                "source", element "ClaimCore.ManagedCopyVerifier"
                "purpose", element "RESTORE_COPY_VERIFIER"
                "verificationEventId", element (eventId.ToString("D"))
                "nonce", element (hex (RandomNumberGenerator.GetBytes(32)))
                "copyId", element (copy.CopyId.ToString("D"))
                "copyEventId", element (copy.EventId.ToString("D"))
                "copyRevision", element revision
                "archiveObjectId", element (Guid.NewGuid().ToString("D"))
                "installationId", element (tip.Identity.InstallationId.ToString("D"))
                "lineageId", element (tip.Identity.LineageId.ToString("D"))
                "witnessEpoch", element tip.Identity.Epoch
                "witnessCutoffSequence", element tip.TipSequence
                "witnessCutoffHash", element (hex tip.TipHash)
                "cluster", element copy.Cluster
                "kind", element copy.Kind
                "postgresSystemId", element copy.PostgresSystemId.Value
                "timeline", element copy.Timeline.Value
                "walSegmentBytes", element copy.WalSegmentBytes.Value
                "backupManifestSha256", optionalHex copy.BackupManifestSha256
                "walStartLsn", optionalText copy.WalStartLsn
                "walEndLsn", optionalText copy.WalEndLsn
                "walSegment", optionalText copy.WalSegment
                "locationCommitment", element (hex copy.LocationCommitment)
                "ciphertextSha256", element (hex copy.CiphertextSha256)
                "ciphertextBytes", element copy.CiphertextBytes
            ]
            @ recoveredFields copy tip
            @ signerFields verifierKeyId verifierHolder checkedAt validUntil
        )

    let signature = algorithm.Sign(key, canonical)
    canonical, signature, checkedAt

let syntheticVerifier (canonical: byte array) signature =
    let verified = VerifiedManagedCopyRestore.FromRechecked(canonical, signature)

    { new IManagedCopyPhysicalVerifier with
        member _.Verify(_, _, _, _, _, _, _: CancellationToken) = Task.FromResult(Some verified)
    }

let unavailableVerifier =
    { new IManagedCopyPhysicalVerifier with
        member _.Verify(_, _, _, _, _, _, _: CancellationToken) = Task.FromResult(None)
    }
