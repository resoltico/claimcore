module internal ClaimCore.IntegrationTests.RestoreRegisteredWalCapture

open System
open System.IO
open System.Security.Cryptography
open System.Text.RegularExpressions
open Expecto
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestorePhysicalProcess
open ClaimCore.IntegrationTests.RestorePhysicalWalRange
open ClaimCore.TestSupport

let private digest path =
    use stream = File.OpenRead(path)
    SHA256.HashData(stream) |> Convert.ToHexStringLower

let private prefix (source: PhysicalCopyCapture) cluster horizon segment =
    let baseCopy =
        source.Objects
        |> List.find (fun item -> item.Cluster = cluster && item.Kind = "BASE")

    requiredSegments
        baseCopy.WalEndLsn.Value
        baseCopy.Timeline
        baseCopy.WalSegmentBytes
        horizon
        segment

let private copiesFor (source: PhysicalCopyCapture) name cluster =
    let template =
        source.Objects
        |> List.find (fun item -> item.Cluster = cluster && item.Kind = "WAL")

    let root = Path.Combine(source.ArchiveRoot, name + "-registered-wal")

    Directory.EnumerateFiles(root, "*.age")
    |> Seq.map (fun path ->
        let file =
            Path.GetFileNameWithoutExtension(path)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failtest "Registered WAL filename is absent")

        if not (Regex.IsMatch(file, "^[0-9A-F]{24}$")) then
            failtest "Registered WAL filename is invalid"

        { template with
            CiphertextPath = path
            RelativePath = name + "-registered-wal/" + file + ".age"
            CiphertextSha256 = digest path
            CiphertextBytes = FileInfo(path).Length
            WalSegment = Some file
        })
    |> Seq.toList

let captureRegisteredWal (source: PhysicalCopyCapture) =
    let primaryHorizon, primarySegment = completedWalEndpoint (adminConnection ())

    let witnessHorizon, witnessSegment =
        completedWalEndpoint (witnessOwnerConnection ())

    let primaryPrefix = prefix source "PRIMARY" primaryHorizon primarySegment
    let witnessPrefix = prefix source "WITNESS" witnessHorizon witnessSegment

    let script =
        Path.Combine(RepositoryRoot.find (), "eng/backup/Capture-RegisteredWal.sh")

    let code, result, stage =
        run
            "bash"
            [
                script
                source.SourcePrimaryContainerId
                source.SourceWitnessContainerId
                source.ScratchRoot
                primaryPrefix
                witnessPrefix
            ]

    if code <> 0 || result <> "registered-wal-capture=synthetic-only" then
        failtest ("Synthetic registered WAL capture failed at " + stage)

    let copies =
        [ "primary", "PRIMARY"; "witness", "WITNESS" ]
        |> List.collect (fun (name, cluster) -> copiesFor source name cluster)
        |> List.sortBy _.RelativePath

    if copies.Length < 2 then
        failtest "Registered WAL capture omitted a cluster"

    {
        Objects = copies
        PrimaryHorizon = primaryHorizon
        WitnessHorizon = witnessHorizon
    }
