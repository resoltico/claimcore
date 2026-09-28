namespace ClaimCore.Postgres

open System

module internal BackupHealthRuntimeWalCoverage =
    let private position (value: string) =
        let parts = value.Split('/')

        if parts.Length <> 2 then
            invalidOp "Backup health WAL LSN is invalid."

        (Convert.ToUInt64(parts[0], 16) <<< 32) ||| Convert.ToUInt64(parts[1], 16)

    let private expected timeline bytes start finish =
        let size = uint64 bytes
        let first = position start / size
        let endpoint = position finish

        if endpoint <= position start then
            invalidOp "Backup health WAL has no registered prefix."

        let last = (endpoint - 1UL) / size

        if last < first || last - first >= 1000UL then
            invalidOp "Backup health WAL segment range is invalid."

        let perLog = 0x100000000UL / size

        [ first..last ]
        |> List.map (fun segment -> $"{timeline:X8}{segment / perLog:X8}{segment % perLog:X8}")

    let verify (claims: BackupHealthClaims) (rows: Map<Guid, BackupHealthRuntimeCopy>) =
        let cluster baseId (wal: BackupHealthWal) timeline =
            let baseCopy = rows[baseId]

            let start =
                baseCopy.BaseEnd
                |> Option.defaultWith (fun () -> invalidOp "Backup health BASE end is absent.")

            let required = expected timeline baseCopy.SegmentBytes start wal.RegisteredHorizon

            let actual =
                wal.CopyIds
                |> List.map (fun id ->
                    let row = rows[id]

                    if row.SegmentBytes <> baseCopy.SegmentBytes then
                        invalidOp "Backup health WAL segment size diverges."

                    row.Segment
                    |> Option.defaultWith (fun () ->
                        invalidOp "Backup health WAL segment identity is absent."))
                |> List.sort

            if actual <> required then
                invalidOp "Backup health WAL prefix is incomplete or on another timeline."

        cluster claims.PrimaryBase.CopyId claims.PrimaryWal claims.PrimaryTimeline
        cluster claims.WitnessBase.CopyId claims.WitnessWal claims.WitnessTimeline
