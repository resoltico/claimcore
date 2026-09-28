module internal ClaimCore.IntegrationTests.RestoreFencedTailSourceDocuments

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text.Json
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Database
open ClaimCore.HostSecurity
open ClaimCore.IntegrationTests.RestorePhysicalProcess
open ClaimCore.IntegrationTests.RestoreWriterHandoffContext
open ClaimCore.TestSupport

let private fields () =
    SortedDictionary<string, objnull>(StringComparer.Ordinal)

let private put (values: SortedDictionary<string, objnull>) name value = values.Add(name, box value)

let private canonical (values: SortedDictionary<string, objnull>) =
    Array.append (JsonSerializer.SerializeToUtf8Bytes(values)) [| byte '\n' |]

let private stamp (value: DateTimeOffset) =
    value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")

let private digest (bytes: byte array) =
    SHA256.HashData(bytes) |> Convert.ToHexStringLower

let private text (name: string) (root: JsonElement) =
    root.GetProperty(name).GetString()
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failtest "Synthetic fenced-tail metadata is incomplete")

let private writeNew root name maximum bytes =
    let path = Path.Combine(root, name)

    match PrivateFileService.writeNew maximum path bytes with
    | Ok() -> path
    | Error _ -> failtest "Synthetic fenced-tail private source could not be created"

let private input (context: SettledW1Context) output =
    let primary, witness = context.Containers

    let ownerRole (source: string) =
        NpgsqlConnectionStringBuilder(source).Username
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failtest "Restored owner role is unavailable")

    let values = fields ()
    put values "format" "claimcore-fenced-tail-capture-input-1"
    put values "primaryContainerId" primary
    put values "witnessContainerId" witness
    put values "primaryOwnerRole" (ownerRole context.Access.Owner)
    put values "witnessOwnerRole" (ownerRole context.Access.WitnessOwner)
    put values "scratchRoot" context.Capture.ScratchRoot
    put values "archiveRoot" context.Capture.ArchiveRoot
    put values "ageIdentityFile" context.Capture.AgeIdentityPath
    put values "outputFile" output
    put values "primaryRegisteredWalHorizon" context.Registered.PrimaryHorizon
    put values "witnessRegisteredWalHorizon" context.Registered.WitnessHorizon
    put values "primarySystemId" context.Facts.PrimarySystemId
    put values "witnessSystemId" context.Facts.WitnessSystemId
    put values "primaryTimeline" context.Facts.PrimaryTimeline
    put values "witnessTimeline" context.Facts.WitnessTimeline
    put values "maximumWalSegments" 1000
    canonical values

let private archiveObject (item: JsonElement) =
    {
        ObjectId = Guid.ParseExact(text "objectId" item, "D")
        Cluster = text "cluster" item
        RelativePath = text "relativePath" item
        CiphertextSha256 = text "sha256" item
        CiphertextBytes = item.GetProperty("bytes").GetInt64()
        Segment = text "segment" item
        SegmentBytes = item.GetProperty("segmentBytes").GetInt32()
    }

let capture (context: SettledW1Context) =
    let root = context.Capture.ScratchRoot
    let output = Path.Combine(root, "fenced-tail-capture.json")
    let config = input context output
    let configPath = writeNew root "fenced-tail-input.json" 32768 config

    let program =
        Path.Combine(RepositoryRoot.find (), "eng/backup/Capture-FencedRecoveryTail.py")

    let code, result, stage = run "python3" [ "-B"; program; "--config"; configPath ]

    if code <> 0 || result <> "fenced-tail-capture=synthetic-only" then
        failtest ("Physical fenced-tail capture refused at " + stage)

    let bytes =
        match PrivateFileService.readBinary 8388608 output with
        | Ok value -> value
        | Error _ -> failtest "Private fenced-tail metadata is unavailable"

    use document = JsonDocument.Parse(bytes)
    let root = document.RootElement

    if
        text "format" root <> "claimcore-fenced-tail-capture-1"
        || text "scope" root <> "synthetic-only"
        || root.GetProperty("realDataReady").GetBoolean()
    then
        failtest "Physical fenced-tail capture scope is invalid"

    let one (name: string) expectedSystem expectedTimeline expectedHorizon =
        let cluster = root.GetProperty(name)

        if
            text "systemId" cluster <> expectedSystem
            || cluster.GetProperty("timeline").GetInt64() <> expectedTimeline
            || text "registeredWalHorizon" cluster <> expectedHorizon
        then
            failtest "Physical fenced-tail cluster identity diverged"

        let objects =
            cluster.GetProperty("objects").EnumerateArray()
            |> Seq.map archiveObject
            |> Seq.toList

        text "finalLsn" cluster, objects

    let primaryFinal, primaryObjects =
        one
            "primary"
            context.Facts.PrimarySystemId
            context.Facts.PrimaryTimeline
            context.Registered.PrimaryHorizon

    let witnessFinal, witnessObjects =
        one
            "witness"
            context.Facts.WitnessSystemId
            context.Facts.WitnessTimeline
            context.Registered.WitnessHorizon

    primaryFinal, witnessFinal, (primaryObjects @ witnessObjects)

let private entry (item: FencedWalObject) =
    let values = fields ()
    put values "objectId" (item.ObjectId.ToString("D"))
    put values "cluster" item.Cluster
    put values "relativePath" item.RelativePath
    put values "ciphertextSha256" item.CiphertextSha256
    put values "ciphertextBytes" item.CiphertextBytes
    put values "walSegment" item.Segment
    put values "walSegmentBytes" item.SegmentBytes
    values

let signedSupplement
    (context: SettledW1Context)
    primaryFinal
    witnessFinal
    (objects: FencedWalObject list)
    =
    let reportSha = context.Report.Evidence.ReportSha256

    let now =
        DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds())

    let validUntil = min context.ValidUntil (now.AddMinutes(10.))

    if validUntil <= now then
        failtest "Post-W1 signed tail validity expired"

    let values = fields ()
    put values "format" "claimcore-fenced-recovery-tail-1"
    put values "scope" "synthetic-only"
    put values "realDataReady" false
    put values "recoveryTailSealed" true
    put values "installationId" (context.Facts.InstallationId.ToString("D"))
    put values "lineageId" (context.Facts.LineageId.ToString("D"))
    put values "epoch" context.Facts.Epoch
    put values "oldGeneration" context.Facts.WriterGeneration
    put values "newGeneration" (context.Facts.WriterGeneration + 1L)
    put values "handoffId" (context.HandoffId.ToString("D"))
    put values "w1Sequence" context.W1Sequence
    put values "w1Hash" (Convert.ToHexStringLower(context.W1Hash))
    put values "reportSha256" reportSha
    put values "fenceReportSha256" (digest context.Fence)
    put values "checkpointSignerKeyId" (context.Input.Index.CheckpointSignerKeyId.ToString("D"))
    put values "primaryRegisteredWalHorizon" context.Registered.PrimaryHorizon
    put values "witnessRegisteredWalHorizon" context.Registered.WitnessHorizon
    put values "primaryFinalWalEndpoint" primaryFinal
    put values "witnessFinalWalEndpoint" witnessFinal
    put values "archiveRoot" context.Capture.ArchiveRoot

    put
        values
        "walObjects"
        (objects |> List.sortBy _.RelativePath |> List.map entry |> List.toArray)

    put values "checkedAt" (stamp now)
    put values "validUntil" (stamp validUntil)
    let body = canonical values
    let signature = SignatureAlgorithm.Ed25519.Sign(context.CheckpointKey, body)
    body, signature
