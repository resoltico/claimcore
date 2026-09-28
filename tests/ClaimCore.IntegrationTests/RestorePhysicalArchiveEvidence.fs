module internal ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open Expecto
open ClaimCore.Database
open ClaimCore.Witness

[<NoEquality; NoComparison>]
type PhysicalArchiveCopy =
    {
        Cluster: string
        Kind: string
        CiphertextPath: string
        RelativePath: string
        CiphertextSha256: string
        CiphertextBytes: int64
        BackupManifestPath: string option
        BackupManifestSha256: string option
        WalStartLsn: string option
        WalEndLsn: string option
        WalSegment: string option
        WalSegmentBytes: int
        PostgresSystemId: string
        Timeline: int64
    }

[<NoEquality; NoComparison>]
type PhysicalCopyCapture =
    {
        ScratchRoot: string
        ArchiveRoot: string
        CiphertextPath: string
        BackupManifestPath: string
        AgeIdentityPath: string
        SigningKeyPath: string
        SignerPublicKey: byte array
        OwnerRole: string
        DatabaseName: string
        WitnessOwnerRole: string
        WitnessDatabaseName: string
        BackupManifestSha256: string
        WalStartLsn: string
        WalEndLsn: string
        WalSegmentBytes: int
        Facts: RestoredPairFacts
        CaptureTip: Snapshot
        Objects: PhysicalArchiveCopy list
        SourcePrimaryContainerId: string
        SourceWitnessContainerId: string
    }

[<NoEquality; NoComparison>]
type RegisteredWalCapture =
    {
        Objects: PhysicalArchiveCopy list
        PrimaryHorizon: string
        WitnessHorizon: string
    }

let private digest path =
    use stream = File.OpenRead(path)
    SHA256.HashData(stream) |> Convert.ToHexStringLower

let private requiredText (name: string) (root: JsonElement) =
    root.GetProperty(name).GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "Physical BASE manifest field is unavailable")

let private archiveOne
    (scratch: string)
    (archiveRoot: string)
    (facts: RestoredPairFacts)
    (name: string)
    =
    let cluster = name.ToUpperInvariant()

    let system, timeline =
        if name = "primary" then
            facts.PrimarySystemId, facts.PrimaryTimeline
        else
            facts.WitnessSystemId, facts.WitnessTimeline

    let manifestPath = Path.Combine(scratch, name, "backup_manifest")
    use document = JsonDocument.Parse(File.ReadAllBytes(manifestPath))
    let manifest = document.RootElement
    let ranges = manifest.GetProperty("WAL-Ranges")

    if ranges.GetArrayLength() <> 1 then
        failtest "Physical BASE has no exact WAL range"

    let range = ranges[0]
    let manifestSha = digest manifestPath
    let walRoot = Path.Combine(scratch, name, "pg_wal")

    let segments =
        Directory.EnumerateFiles(walRoot)
        |> Seq.filter (fun path ->
            Path.GetFileName(path)
            |> Option.ofObj
            |> Option.exists (fun value -> Regex.IsMatch(value, "^[0-9A-F]{24}$")))
        |> Seq.sort
        |> Seq.toList

    if segments.IsEmpty then
        failtest "Physical BASE has no WAL segment"

    let segmentBytes = FileInfo(segments.Head).Length |> int

    let archiveObject name kind manifestSha startLsn endLsn segment =
        let path = Path.Combine(archiveRoot, name)

        let item: PhysicalArchiveCopy =
            {
                Cluster = cluster
                Kind = kind
                CiphertextPath = path
                RelativePath = name
                CiphertextSha256 = digest path
                CiphertextBytes = FileInfo(path).Length
                BackupManifestPath = if kind = "BASE" then Some manifestPath else None
                BackupManifestSha256 = manifestSha
                WalStartLsn = startLsn
                WalEndLsn = endLsn
                WalSegment = segment
                WalSegmentBytes = segmentBytes
                PostgresSystemId = system
                Timeline = timeline
            }

        item

    let baseCopy =
        archiveObject
            (name + ".tar.age")
            "BASE"
            (Some manifestSha)
            (Some(requiredText "Start-LSN" range))
            (Some(requiredText "End-LSN" range))
            None

    let walCopies =
        segments
        |> List.map (fun source ->
            let segment =
                Path.GetFileName(source)
                |> Option.ofObj
                |> Option.defaultWith (fun () -> failtest "Physical WAL segment name is missing")

            archiveObject (name + "-wal/" + segment + ".age") "WAL" None None None (Some segment))

    baseCopy, walCopies

let capture
    scratch
    ownerRole
    database
    witnessRole
    witnessDatabase
    keyPath
    publicKey
    facts
    captureTip
    sourcePrimary
    sourceWitness
    =
    let archiveRoot = Path.Combine(scratch, "archive")
    let primary, primaryWal = archiveOne scratch archiveRoot facts "primary"
    let witness, witnessWal = archiveOne scratch archiveRoot facts "witness"

    {
        ScratchRoot = scratch
        ArchiveRoot = archiveRoot
        CiphertextPath = primary.CiphertextPath
        BackupManifestPath = primary.BackupManifestPath.Value
        AgeIdentityPath = Path.Combine(scratch, "identity.age")
        SigningKeyPath = keyPath
        SignerPublicKey = publicKey
        OwnerRole = ownerRole
        DatabaseName = database
        WitnessOwnerRole = witnessRole
        WitnessDatabaseName = witnessDatabase
        BackupManifestSha256 = primary.BackupManifestSha256.Value
        WalStartLsn = primary.WalStartLsn.Value
        WalEndLsn = primary.WalEndLsn.Value
        WalSegmentBytes = primary.WalSegmentBytes
        Facts = facts
        CaptureTip = captureTip
        Objects = (primary :: witness :: (primaryWal @ witnessWal)) |> List.sortBy _.RelativePath
        SourcePrimaryContainerId = sourcePrimary
        SourceWitnessContainerId = sourceWitness
    }
