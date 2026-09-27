namespace ClaimCore.Postgres

open System
open System.Text.Json

module internal BackupHealthDocumentParts =
    open BackupHealthFields

    let private require fields value =
        if not (BackupHealthCanonical.exact value fields) then
            invalidOp "Backup health nested document is not exact."

    let baseCopy (value: JsonElement) =
        require [ "copyId"; "revision"; "physicalReceiptSha256"; "verifiedAt" ] value
        let revision = number value "revision"

        if revision < 2L then
            invalidOp "Backup health BASE has no physical verification revision."

        {
            CopyId = uuid value "copyId"
            Revision = revision
            PhysicalReceiptSha256 = sha value "physicalReceiptSha256"
            VerifiedAt = instant value "verifiedAt"
        }

    let wal (value: JsonElement) =
        require [ "copyIds"; "registeredHorizon"; "archiveInspectionSha256"; "verifiedAt" ] value
        let raw = value.GetProperty("copyIds")

        if
            raw.ValueKind <> JsonValueKind.Array
            || raw.GetArrayLength() < 1
            || raw.GetArrayLength() > 1000
        then
            invalidOp "Backup health WAL copy set is invalid."

        let ids =
            raw.EnumerateArray()
            |> Seq.map (fun item ->
                if item.ValueKind <> JsonValueKind.String then
                    invalidOp "Backup health WAL copy identity is invalid."

                let source =
                    item.GetString()
                    |> Option.ofObj
                    |> Option.defaultWith (fun () ->
                        invalidOp "Backup health WAL copy identity is absent.")

                let parsed = Guid.ParseExact(source, "D")

                if parsed = Guid.Empty || parsed.ToString("D") <> source then
                    invalidOp "Backup health WAL copy identity is noncanonical."

                parsed)
            |> Seq.toList

        if ids <> (ids |> List.sort) || (ids |> List.distinct |> List.length) <> ids.Length then
            invalidOp "Backup health WAL copy IDs are not a sorted exact set."

        {
            CopyIds = ids
            RegisteredHorizon = lsn value "registeredHorizon"
            ArchiveInspectionSha256 = sha value "archiveInspectionSha256"
            VerifiedAt = instant value "verifiedAt"
        }

    let checkpoint (value: JsonElement) =
        require [ "sequence"; "hash"; "objectSha256"; "verifiedAt" ] value
        let sequence = number value "sequence"

        if sequence < 0L then
            invalidOp "Backup health checkpoint sequence is invalid."

        {
            Sequence = sequence
            Hash = sha value "hash"
            ObjectSha256 = sha value "objectSha256"
            VerifiedAt = instant value "verifiedAt"
        }

    let restored (value: JsonElement) =
        require [ "reportSha256"; "witnessCutoff"; "witnessCutoffHash"; "verifiedAt" ] value
        let cutoff = number value "witnessCutoff"

        if cutoff < 0L then
            invalidOp "Backup health restored-pair cutoff is invalid."

        {
            ReportSha256 = sha value "reportSha256"
            WitnessCutoff = cutoff
            WitnessCutoffHash = sha value "witnessCutoffHash"
            VerifiedAt = instant value "verifiedAt"
        }

    let private genesis (value: BackupHealthWriterFence) =
        if
            value.HandoffId.IsSome
            || value.W1Sequence.IsSome
            || value.W1Hash.IsSome
            || value.ActivationSequence.IsSome
            || value.ActivationHash.IsSome
            || value.OldGeneration.IsSome
            || value.NewGeneration.IsSome
        then
            invalidOp "Backup health genesis fence has handoff evidence."

    let private handoff (value: BackupHealthWriterFence) generation tip =
        match
            value.W1Sequence,
            value.ActivationSequence,
            value.OldGeneration,
            value.NewGeneration,
            value.HandoffId,
            value.W1Hash,
            value.ActivationHash
        with
        | Some w1, Some activated, Some oldGeneration, Some newGeneration, Some _, Some _, Some _ when
            w1 > 0L
            && activated > w1
            && activated <= tip
            && oldGeneration = generation - 1L
            && newGeneration = generation
            ->
            ()
        | _ -> invalidOp "Backup health W1/W2 fence is incomplete."

    let fence (value: JsonElement) generation tip =
        require
            [
                "kind"
                "handoffId"
                "w1Sequence"
                "w1Hash"
                "activationSequence"
                "activationHash"
                "oldGeneration"
                "newGeneration"
            ]
            value

        let parsed =
            {
                Kind = text value "kind"
                HandoffId = optionalUuid value "handoffId"
                W1Sequence = optionalNumber value "w1Sequence"
                W1Hash = optionalSha value "w1Hash"
                ActivationSequence = optionalNumber value "activationSequence"
                ActivationHash = optionalSha value "activationHash"
                OldGeneration = optionalNumber value "oldGeneration"
                NewGeneration = optionalNumber value "newGeneration"
            }

        match parsed.Kind with
        | "GENESIS" when generation = 1L -> genesis parsed
        | "HANDOFF" when generation > 1L -> handoff parsed generation tip
        | _ -> invalidOp "Backup health writer fence kind is invalid."

        parsed
