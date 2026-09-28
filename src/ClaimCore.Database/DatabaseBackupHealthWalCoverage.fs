namespace ClaimCore.Database

open System

module internal DatabaseBackupHealthWalCoverage =
    let private position (value: string) =
        let halves = value.Split('/')

        if halves.Length <> 2 then
            invalidOp "Backup health WAL endpoint is invalid."

        (Convert.ToUInt64(halves[0], 16) <<< 32) ||| Convert.ToUInt64(halves[1], 16)

    let private expected timeline segmentBytes start finish =
        let size = uint64 segmentBytes
        let first = position start / size
        let endpoint = position finish

        if endpoint <= position start then
            invalidOp "Backup health WAL horizon does not follow BASE."

        let last = (endpoint - 1UL) / size

        if last < first || last - first >= 1000UL then
            invalidOp "Backup health WAL range exceeds reviewed bound."

        let perLog = 0x100000000UL / size

        [ first..last ]
        |> List.map (fun number -> $"{timeline:X8}{number / perLog:X8}{number % perLog:X8}")

    let verify (evidence: BackupHealthEvidence) =
        let cluster name =
            let baseCopy =
                evidence.Objects
                |> List.find (fun item -> item.Cluster = name && item.Kind = "BASE")

            let wal =
                evidence.Objects
                |> List.filter (fun item -> item.Cluster = name && item.Kind = "WAL")

            let horizon = wal.Head.WalHorizon

            let required =
                expected baseCopy.Timeline baseCopy.WalSegmentBytes baseCopy.WalHorizon horizon

            let actual = wal |> List.choose _.WalSegment |> List.sort

            if actual <> required then
                invalidOp "Backup health WAL prefix is incomplete or has another timeline."

        cluster "PRIMARY"
        cluster "WITNESS"
