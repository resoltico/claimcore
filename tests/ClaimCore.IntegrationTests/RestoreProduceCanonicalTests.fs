module ClaimCore.IntegrationTests.RestoreProduceCanonicalTests

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Database

let private digest value = String(value, 64)

let private archiveObjects () =
    [
        for cluster in [ "PRIMARY"; "WITNESS" ] do
            for kind in [ "BASE"; "WAL" ] do
                {
                    ObjectId = Guid.NewGuid()
                    CopyId = Guid.NewGuid()
                    Cluster = cluster
                    Kind = kind
                    RelativePath = (cluster + "-" + kind + ".age").ToLowerInvariant()
                    Sha256 = digest '2'
                    Bytes = 100L
                    WalSegment =
                        if kind = "WAL" then
                            Some "000000010000000000000000"
                        else
                            None
                    WalSegmentBytes = if kind = "WAL" then Some 16777216 else None
                }
    ]
    |> List.sortBy _.RelativePath

let private publication installation lineage reportKey checkpointKey : TrustedRestorePublication =
    {
        ManifestSha256 = digest 'a'
        VerifierBinarySha256 = digest 'b'
        ReportSignerKeyId = reportKey
        CheckpointSignerKeyId = checkpointKey
        InstallationId = installation
        LineageId = lineage
        Epoch = 1L
        WriterGeneration = 2L
        WitnessCutoff = 12L
        WitnessCutoffHash = digest 'c'
    }

let private approvers () =
    [
        for revision in [ 1L; 2L ] do
            {
                ActorId = Guid.NewGuid()
                GrantRevision = revision
                ApprovalEventId = Guid.NewGuid()
            }
    ]

let private archiveCustody archiveSet objects =
    {
        ObjectId = archiveSet
        Sha256 = DatabaseRestoreArchiveObjects.rootDigest objects
        Bytes = DatabaseRestoreArchiveObjects.totalBytes objects
    }

let private claims
    installation
    lineage
    cycle
    reportKey
    checkpointKey
    archiveSet
    checkpointObject
    objects
    now
    : RestoreReportClaims =
    {
        Scope = "synthetic-only"
        InstallationId = installation
        LineageId = lineage
        Epoch = 1L
        CycleId = cycle
        BackupCaptureSequence = 8L
        BackupCaptureHash = digest '7'
        WitnessCutoff = 12L
        WitnessCutoffHash = digest 'c'
        PrimarySystemId = "111111111111"
        PrimaryTimeline = 1L
        WitnessSystemId = "222222222222"
        WitnessTimeline = 1L
        PrimaryRegisteredWalHorizon = "0/AAB"
        WitnessRegisteredWalHorizon = "0/DDE"
        SignerKeyId = reportKey
        VerifierBinarySha256 = digest 'b'
        EvidenceIndexSha256 = digest '0'
        CheckpointSha256 = digest 'd'
        SignedInventoryFileSha256 = digest 'e'
        QuiescentBarrierSha256 = digest 'f'
        CatalogManifestSha256 = digest 'a'
        AuthorityRevision = 2L
        AuthorizedApprovers = approvers ()
        ArchiveCustody = archiveCustody archiveSet objects
        CheckpointCustody =
            {
                ObjectId = checkpointObject
                Sha256 = digest '3'
                Bytes = 200L
            }
        CustodyKeyId = checkpointKey
        CustodyPublicKeySha256 = digest '4'
        CheckedAt = now
        ValidUntil = now.AddMinutes(20.0)
    }

let private index
    (claims: RestoreReportClaims)
    archiveRoot
    checkpointKey
    objects
    : RestoreEvidenceIndex =
    {
        InstallationId = claims.InstallationId
        LineageId = claims.LineageId
        Epoch = claims.Epoch
        CycleId = claims.CycleId
        BackupCaptureSequence = claims.BackupCaptureSequence
        BackupCaptureHash = claims.BackupCaptureHash
        PublicationManifestSha256 = digest 'a'
        ArchiveRoot = archiveRoot
        ArchiveSetId = claims.ArchiveCustody.ObjectId
        ArchiveObjects = objects
        PrimaryCaptureWalEndpoint = "0/AAA"
        WitnessCaptureWalEndpoint = "0/DDD"
        PrimaryRegisteredWalHorizon = claims.PrimaryRegisteredWalHorizon
        WitnessRegisteredWalHorizon = claims.WitnessRegisteredWalHorizon
        PrimaryWalEndpoint = "0/ABC"
        WitnessWalEndpoint = "0/DEF"
        CheckpointRoot = "/synthetic/checkpoints"
        CheckpointFile = "/synthetic/checkpoints/cycle.json"
        CheckpointSignatureFile = "/synthetic/checkpoints/cycle.sig"
        ManifestFile = "/synthetic/archive/manifest.json"
        ManifestSignatureFile = "/synthetic/archive/manifest.sig"
        ManifestSha256 = digest '1'
        BarrierFile = "/synthetic/archive/barrier.json"
        BarrierSignatureFile = "/synthetic/archive/barrier.sig"
        InventoryRoot = "/synthetic/inventory"
        InventorySnapshotFile = "/synthetic/inventory/snapshot.json"
        InventorySnapshotSignatureFile = "/synthetic/inventory/snapshot.sig"
        CheckpointSignerKeyId = checkpointKey
        CheckpointObjectId = claims.CheckpointCustody.ObjectId
    }

let internal specimen () =
    let installation = Guid.NewGuid()
    let lineage = Guid.NewGuid()
    let cycle = Guid.NewGuid()
    let reportKey = Guid.NewGuid()
    let checkpointKey = Guid.NewGuid()
    let objects = archiveObjects ()
    let now = DateTimeOffset(DateTime.UtcNow.Date.AddHours(12.0), TimeSpan.Zero)

    let claims =
        claims
            installation
            lineage
            cycle
            reportKey
            checkpointKey
            (Guid.NewGuid())
            (Guid.NewGuid())
            objects
            now

    let index = index claims "/synthetic/archive" checkpointKey objects
    let publication = publication installation lineage reportKey checkpointKey
    let bytes = DatabaseRestoreProduceCanonical.evidenceIndex claims index publication
    let sha = SHA256.HashData(bytes) |> Convert.ToHexStringLower

    { claims with
        EvidenceIndexSha256 = sha
    },
    index,
    publication

let private exactCanonical =
    testCase
        "[CC-BACKUP-001] restore producer emits exact canonical signed payload inputs"
        (fun _ ->
            let claims, index, publication = specimen ()

            let produced = DatabaseRestoreProduceCanonical.produce claims index publication

            Expect.isTrue (produced.Report.Length > 0) "Report bytes are emitted"

            Expect.equal
                produced.Report[produced.Report.Length - 1]
                (byte '\n')
                "One LF is present"

            Expect.isSome
                (DatabaseRestoreReportClaims.parse produced.Report claims.CheckedAt)
                "The independent report parser accepts the exact bytes"

            Expect.isSome
                (DatabaseRestoreEvidenceIndex.parse produced.EvidenceIndex claims)
                "The independent index parser accepts the exact bytes"

            let changed =
                { claims with
                    EvidenceIndexSha256 = digest '9'
                }

            Expect.throws
                (fun () ->
                    DatabaseRestoreProduceCanonical.produce changed index publication |> ignore)
                "A report cannot bind a different index digest")

let private objectSetRefusals =
    testCase
        "[CC-BACKUP-001] restore index refuses path escape and archive object substitution"
        (fun _ ->
            let claims, index, publication = specimen ()
            let first = index.ArchiveObjects.Head

            for changed in
                [
                    { first with
                        RelativePath = "../outside.age"
                    }
                    { first with
                        RelativePath = "nested//outside.age"
                    }
                    { first with Sha256 = digest '9' }
                ] do
                let objects = changed :: index.ArchiveObjects.Tail
                let candidate = { index with ArchiveObjects = objects }

                Expect.throws
                    (fun () ->
                        DatabaseRestoreProduceCanonical.produce claims candidate publication
                        |> ignore)
                    "An escaped or substituted archive object cannot match signed custody"

            let wrongKey =
                { claims with
                    CustodyKeyId = Guid.NewGuid()
                }

            Expect.throws
                (fun () ->
                    DatabaseRestoreProduceCanonical.produce wrongKey index publication |> ignore)
                "A report cannot substitute its checkpoint custody key")

let private separateCutoffs =
    testCase "[CC-BACKUP-001] signed backup capture cannot equal later audited cutoff" (fun _ ->
        let claims, _, _ = specimen ()

        let impossible =
            { claims with
                BackupCaptureSequence = claims.WitnessCutoff
                BackupCaptureHash = claims.WitnessCutoffHash
            }

        let bytes = DatabaseRestoreProduceCanonical.report impossible

        Expect.isNone
            (DatabaseRestoreReportClaims.parse bytes claims.CheckedAt)
            "A backup cannot carry its own later witnessed copy registrations")

let private wrongPhaseFlag =
    testCase "[CC-BACKUP-001] audit report refuses cutover fence flag substitution" (fun _ ->
        let claims, _, _ = specimen ()

        let bytes = DatabaseRestoreProduceCanonical.report claims

        use document = JsonDocument.Parse(bytes)
        let fields = SortedDictionary<string, JsonElement>(StringComparer.Ordinal)

        for property in document.RootElement.EnumerateObject() do
            fields[property.Name] <- property.Value.Clone()

        fields.Remove("quiescentAuditBarrierVerified") |> ignore
        fields["writerFenceVerified"] <- JsonSerializer.SerializeToElement(true)
        let replaced = Encoding.ASCII.GetBytes(JsonSerializer.Serialize(fields) + "\n")

        Expect.isNone
            (DatabaseRestoreReportClaims.parse replaced claims.CheckedAt)
            "A future W1 fence claim cannot impersonate the pre-W1 audit barrier")

let tests =
    testList
        "restore producer canonical bytes"
        [ exactCanonical; objectSetRefusals; separateCutoffs; wrongPhaseFlag ]
