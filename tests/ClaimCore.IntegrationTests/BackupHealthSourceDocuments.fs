module internal ClaimCore.IntegrationTests.BackupHealthSourceDocuments

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text.Json
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.BackupHealthSourceMaterial

[<NoEquality; NoComparison>]
type SyntheticHealthDocuments =
    {
        Root: string
        Policy: BackupHealthPolicy
        Evidence: BackupHealthEvidence
        Claims: BackupHealthClaims
        Loaded: LoadedBackupHealthEvidence
        RoleKeys: Key list
        IssuerKey: Key
    }

let private baseCopy (item: SortedDictionary<string, objnull>) =
    let value = fields ()
    put value "copyId" item["copyId"]
    put value "revision" 2
    put value "physicalReceiptSha256" item["physicalReceiptSha256"]
    put value "verifiedAt" item["verifiedAt"]
    value

let private wal source now (item: SortedDictionary<string, objnull>) =
    let value = fields ()
    put value "copyIds" [| item["copyId"] |]
    put value "registeredHorizon" "0/100"
    put value "archiveInspectionSha256" (sha source)
    put value "verifiedAt" (stamp now)
    value

let private genesisFence () =
    let fence = fields ()
    put fence "kind" "GENESIS"

    for name in
        [
            "handoffId"
            "w1Sequence"
            "w1Hash"
            "activationSequence"
            "activationHash"
            "oldGeneration"
            "newGeneration"
        ] do
        put fence name null

    fence

let private selected names (source: SortedDictionary<string, objnull>) =
    let value = fields ()

    for name in names do
        put value name source[name]

    value

let private certificateIdentity
    (value: SortedDictionary<string, objnull>)
    (installation: Guid, lineage: Guid)
    (now: DateTimeOffset)
    =
    put value "format" "claimcore-backup-health-1"
    put value "source" "ClaimCore.Database"
    put value "scope" "full"
    put value "installationId" (installation.ToString("D"))
    put value "lineageId" (lineage.ToString("D"))
    put value "epoch" 1
    put value "writerGeneration" 1
    put value "policyId" "reviewed-test-recovery"
    put value "authorityRevision" 7
    put value "witnessTipSequence" 20
    put value "witnessTipHash" (String('c', 64))
    put value "checkedAt" (stamp now)
    put value "validUntil" (stamp (now.AddSeconds(60.)))
    put value "maximumBackupAgeSeconds" 3600
    put value "maximumWalLagSeconds" 3600
    put value "maximumCheckpointAgeSeconds" 3600
    put value "maximumRestoreTestAgeSeconds" 3600
    put value "restoreHorizonSeconds" 7200
    put value "primarySystemId" "1111111111111111111"
    put value "primaryTimeline" 1
    put value "witnessSystemId" "2222222222222222222"
    put value "witnessTimeline" 1

let private certificateBytes
    (identity: Guid * Guid)
    (now: DateTimeOffset)
    (source: byte array)
    (objects: SortedDictionary<string, objnull> array)
    (checkpoint: SortedDictionary<string, objnull>)
    (restored: SortedDictionary<string, objnull>)
    (issuerId: Guid)
    (holderId: Guid)
    =
    let value = fields ()
    certificateIdentity value identity now
    put value "primaryBase" (baseCopy objects[0])
    put value "witnessBase" (baseCopy objects[1])
    put value "primaryWal" (wal source now objects[2])
    put value "witnessWal" (wal source now objects[3])

    put
        value
        "checkpoint"
        (selected [ "sequence"; "hash"; "objectSha256"; "verifiedAt" ] checkpoint)

    put
        value
        "testRestore"
        (selected [ "reportSha256"; "witnessCutoff"; "witnessCutoffHash"; "verifiedAt" ] restored)

    put value "knownCopyInventorySha256" (String('a', 64))
    put value "artifactCutoffSequence" 16
    put value "writerFence" (genesisFence ())
    put value "signerKeyId" (issuerId.ToString("D"))
    put value "signerHolderActorId" (holderId.ToString("D"))
    canonical value

let private physicalMaterial archive checkpointRoot restoreRoot cycleId now =
    let primaryBase, primaryId =
        archiveObject archive "primary-base" "PRIMARY" "BASE" "1111111111111111111" None now

    let witnessBase, witnessId =
        archiveObject archive "witness-base" "WITNESS" "BASE" "2222222222222222222" None now

    let primaryWal, _ =
        archiveObject
            archive
            "primary-wal"
            "PRIMARY"
            "WAL"
            "1111111111111111111"
            (Some "000000010000000000000000")
            now

    let witnessWal, _ =
        archiveObject
            archive
            "witness-wal"
            "WITNESS"
            "WAL"
            "2222222222222222222"
            (Some "000000010000000000000000")
            now

    let objects = [| primaryBase; witnessBase; primaryWal; witnessWal |]
    let checkpoint, _ = checkpointObject checkpointRoot cycleId now
    let _, reportSha, _ = objectFile restoreRoot "report.json" [| 7uy |]
    let _, auditSha, _ = objectFile restoreRoot "audit.json" [| 8uy |]
    let restored = restoreObject primaryId witnessId reportSha auditSha now
    objects, checkpoint, restored

let create () =
    let root = root ()
    let archive = directory root "archive"
    let checkpointRoot = directory root "checkpoint"
    let restoreRoot = directory root "restore"
    let keys = [ for _ in 1..3 -> Key.Create(SignatureAlgorithm.Ed25519) ]
    let issuer = Key.Create(SignatureAlgorithm.Ed25519)

    let now =
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds())

    let identity = Guid.NewGuid(), Guid.NewGuid()
    let cycleId, leaseId = Guid.NewGuid(), Guid.NewGuid()
    let policyRaw = policyBytes archive checkpointRoot restoreRoot keys

    let policy =
        BackupHealthPolicyCodec.parse policyRaw (sha policyRaw)
        |> Option.defaultWith (fun () -> invalidOp "Synthetic health policy is invalid.")

    let objects, checkpoint, restored =
        physicalMaterial archive checkpointRoot restoreRoot cycleId now

    let source = sourceBytes identity cycleId leaseId now objects checkpoint restored
    let issuerId, holderId = Guid.NewGuid(), Guid.NewGuid()

    let certificate =
        certificateBytes identity now source objects checkpoint restored issuerId holderId

    let loaded =
        {
            PolicyBytes = policyRaw
            SourceBytes = source
            ArchiveSignature = SignatureAlgorithm.Ed25519.Sign(keys[0], source)
            CheckpointSignature = SignatureAlgorithm.Ed25519.Sign(keys[1], source)
            RestoreSignature = SignatureAlgorithm.Ed25519.Sign(keys[2], source)
            CertificateBytes = certificate
            CertificateSignature = SignatureAlgorithm.Ed25519.Sign(issuer, certificate)
        }

    let evidence =
        DatabaseBackupHealthEvidenceClaims.parse source now
        |> Option.defaultWith (fun () -> invalidOp "Synthetic independent source is invalid.")

    let claims =
        BackupHealthCertificate.parse certificate now
        |> Option.defaultWith (fun () -> invalidOp "Synthetic signed health claims are invalid.")

    {
        Root = root
        Policy = policy
        Evidence = evidence
        Claims = claims
        Loaded = loaded
        RoleKeys = keys
        IssuerKey = issuer
    }

let dispose value =
    value.RoleKeys |> List.iter (fun item -> item.Dispose())
    value.IssuerKey.Dispose()
    Directory.Delete(value.Root, true)
