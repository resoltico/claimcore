module internal ClaimCore.IntegrationTests.RestorePhysicalWalRetention

open System
open Expecto
open ClaimCore.IntegrationTests.RestorePhysicalArchiveEvidence
open ClaimCore.IntegrationTests.RestorePhysicalProcess

let private firstSegment (horizon: string) (timeline: int64) (segmentBytes: int) =
    let halves = horizon.Split('/')

    if halves.Length <> 2 || segmentBytes <= 0 then
        failtest "Registered WAL horizon is invalid"

    let position =
        (Convert.ToUInt64(halves[0], 16) <<< 32) ||| Convert.ToUInt64(halves[1], 16)

    let segment = position / uint64 segmentBytes
    let perLog = 0x100000000UL / uint64 segmentBytes
    $"{timeline:X8}{segment / perLog:X8}{segment % perLog:X8}"

let requireRegisteredHorizonSegments
    (capture: PhysicalCopyCapture)
    (registered: RegisteredWalCapture)
    (primaryContainer: string, witnessContainer: string)
    =
    let check cluster horizon container =
        let source =
            capture.Objects
            |> List.find (fun item -> item.Cluster = cluster && item.Kind = "BASE")

        let segment = firstSegment horizon source.Timeline source.WalSegmentBytes
        let path = "/var/lib/postgresql/18/docker/pg_wal/" + segment

        let code, _, _ =
            run "docker" [ "exec"; "-u"; "postgres"; container; "test"; "-f"; path ]

        if code <> 0 then
            failtest "Restored pair did not retain exact registered-horizon WAL segment"

    check "PRIMARY" registered.PrimaryHorizon primaryContainer
    check "WITNESS" registered.WitnessHorizon witnessContainer
