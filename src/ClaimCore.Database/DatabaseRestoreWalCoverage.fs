namespace ClaimCore.Database

open System
open System.Globalization

/// The signed archive index must contain every same-timeline WAL segment needed between the
/// captured base image and the later, fully audited restored witness/primary cutoff.
module internal DatabaseRestoreWalCoverage =
    let private hex (value: string) =
        UInt64.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)

    let private position (value: string) =
        let parts = value.Split('/')

        if parts.Length <> 2 || parts[0].Length > 8 || parts[1].Length > 8 then
            invalidOp "Restore WAL endpoint is invalid."

        let high = hex parts[0]
        let low = hex parts[1]

        if high > 0xFFFFFFFFUL || low > 0xFFFFFFFFUL then
            invalidOp "Restore WAL endpoint is invalid."

        (high <<< 32) ||| low

    let private segmentNumber (name: string) (segmentBytes: int) =
        let segmentsPerLog = 0x100000000UL / uint64 segmentBytes
        let log = hex (name.Substring(8, 8))
        let slot = hex (name.Substring(16, 8))

        if slot >= segmentsPerLog then
            invalidOp "Restore WAL segment number is invalid."

        log * segmentsPerLog + slot

    let private verifyRange
        cluster
        timeline
        start
        horizon
        (segments: (string * string * int) list)
        =
        let selected =
            segments |> List.filter (fun (itemCluster, _, _) -> itemCluster = cluster)

        let sizes = selected |> List.map (fun (_, _, bytes) -> bytes) |> List.distinct

        if sizes.Length <> 1 || selected.Length < 1 || selected.Length > 1000 then
            invalidOp "Restored WAL segment size or inventory is incomplete."

        let segmentBytes = sizes.Head
        let requiredFirst = start / uint64 segmentBytes
        let requiredLast = (horizon - 1UL) / uint64 segmentBytes
        let requiredCount = requiredLast - requiredFirst + 1UL

        if requiredCount > 1000UL then
            invalidOp "Restored WAL coverage exceeds the reviewed bound."

        let actual =
            selected
            |> List.map (fun (_, name, _) ->
                let observedTimeline = hex (name.Substring(0, 8))

                if observedTimeline <> uint64 timeline then
                    invalidOp "Restored WAL timeline differs from the captured pair."

                segmentNumber name segmentBytes)
            |> List.sort

        if
            actual.Length <> int requiredCount
            || (actual
                |> List.mapi (fun offset value -> value = requiredFirst + uint64 offset)
                |> List.exists not)
        then
            invalidOp "Restored WAL archive has a missing, extra, or duplicate segment."

    let private verifyOne cluster timeline capture registered audited objects =
        let start = position capture
        let horizon = position registered
        let finished = position audited

        if horizon <= start || finished <= horizon then
            invalidOp "Restore report lacks a separate registered horizon and open recovery tail."

        let segments =
            objects
            |> List.choose (fun (item: RestoreArchiveObject) ->
                if item.Kind <> "WAL" then
                    None
                else
                    match item.WalSegment, item.WalSegmentBytes with
                    | Some name, Some bytes -> Some(item.Cluster, name, bytes)
                    | _ -> invalidOp "Restored WAL segment identity is absent.")

        verifyRange cluster timeline start horizon segments

    let verify (index: RestoreEvidenceIndex) (report: RestoreReportClaims) =
        verifyOne
            "PRIMARY"
            report.PrimaryTimeline
            index.PrimaryCaptureWalEndpoint
            index.PrimaryRegisteredWalHorizon
            index.PrimaryWalEndpoint
            index.ArchiveObjects

        verifyOne
            "WITNESS"
            report.WitnessTimeline
            index.WitnessCaptureWalEndpoint
            index.WitnessRegisteredWalHorizon
            index.WitnessWalEndpoint
            index.ArchiveObjects

    let verifyFencedTail
        (report: RestoreReportClaims)
        primaryFinal
        witnessFinal
        (segments: (string * string * int) list)
        =
        let primaryStart = position report.PrimaryRegisteredWalHorizon
        let witnessStart = position report.WitnessRegisteredWalHorizon
        let primaryEnd = position primaryFinal
        let witnessEnd = position witnessFinal

        if primaryEnd <= primaryStart || witnessEnd <= witnessStart then
            invalidOp "Fenced recovery tail does not advance both WAL endpoints."

        verifyRange "PRIMARY" report.PrimaryTimeline primaryStart primaryEnd segments
        verifyRange "WITNESS" report.WitnessTimeline witnessStart witnessEnd segments
