namespace ClaimCore.Database

open System
open System.Globalization
open System.Text.Json

[<NoEquality; NoComparison>]
type internal FencedWalObject =
    {
        ObjectId: Guid
        Cluster: string
        RelativePath: string
        CiphertextSha256: string
        CiphertextBytes: int64
        Segment: string
        SegmentBytes: int
    }

[<NoEquality; NoComparison>]
type internal FencedTailClaims =
    {
        Scope: string
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        OldGeneration: int64
        NewGeneration: int64
        HandoffId: Guid
        W1Sequence: int64
        W1Hash: string
        ReportSha256: string
        FenceReportSha256: string
        CheckpointSignerKeyId: Guid
        PrimaryRegisteredWalHorizon: string
        WitnessRegisteredWalHorizon: string
        PrimaryFinalWalEndpoint: string
        WitnessFinalWalEndpoint: string
        ArchiveRoot: string
        WalObjects: FencedWalObject list
        CheckedAt: DateTimeOffset
        ValidUntil: DateTimeOffset
    }

/// Exact signed W1 tail claim. Its fields are evidence targets, never a readiness flag.
module internal DatabaseRestoreFencedTailClaims =
    let private fields =
        [
            "format"
            "scope"
            "realDataReady"
            "recoveryTailSealed"
            "installationId"
            "lineageId"
            "epoch"
            "oldGeneration"
            "newGeneration"
            "handoffId"
            "w1Sequence"
            "w1Hash"
            "reportSha256"
            "fenceReportSha256"
            "checkpointSignerKeyId"
            "primaryRegisteredWalHorizon"
            "witnessRegisteredWalHorizon"
            "primaryFinalWalEndpoint"
            "witnessFinalWalEndpoint"
            "archiveRoot"
            "walObjects"
            "checkedAt"
            "validUntil"
        ]

    let private objectFields =
        [
            "objectId"
            "cluster"
            "relativePath"
            "ciphertextSha256"
            "ciphertextBytes"
            "walSegment"
            "walSegmentBytes"
        ]

    let private text name root =
        DatabaseRestoreCanonical.text name root
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Fenced tail text is unavailable.")

    let private uuid name root =
        let raw = text name root

        match Guid.TryParseExact(raw, "D") with
        | true, value when value <> Guid.Empty && value.ToString("D") = raw -> value
        | _ -> invalidOp "Fenced tail identity is invalid."

    let private digest name root =
        let raw = text name root

        if
            raw.Length <> 64
            || raw
               |> Seq.exists (fun value ->
                   not (('0' <= value && value <= '9') || ('a' <= value && value <= 'f')))
        then
            invalidOp "Fenced tail digest is invalid."

        raw

    let private instant name root =
        let raw = text name root
        let mutable value = DateTimeOffset.MinValue

        if
            not (
                DateTimeOffset.TryParseExact(
                    raw,
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    &value
                )
            )
            || value.Offset <> TimeSpan.Zero
            || value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) <> raw
        then
            invalidOp "Fenced tail time is invalid."

        value

    let private relative (value: string) =
        value.Length > 0
        && value.Length <= 512
        && not (value.StartsWith("/", StringComparison.Ordinal))
        && value.Split('/')
           |> Array.forall (fun part ->
               part.Length > 0
               && part <> "."
               && part <> ".."
               && part
                  |> Seq.forall (fun character ->
                      Char.IsAsciiLetterOrDigit(character)
                      || character = '-'
                      || character = '_'
                      || character = '.'))

    let private walObject (root: JsonElement) =
        if not (DatabaseRestoreCanonical.exactProperties objectFields root) then
            invalidOp "Fenced WAL object shape is invalid."

        let cluster = text "cluster" root
        let path = text "relativePath" root
        let name = text "walSegment" root
        let bytes = DatabaseRestoreCanonical.number "walSegmentBytes" root
        let length = DatabaseRestoreCanonical.number "ciphertextBytes" root

        if
            (cluster <> "PRIMARY" && cluster <> "WITNESS")
            || not (relative path)
            || name.Length <> 24
            || name
               |> Seq.exists (fun character ->
                   not (
                       ('0' <= character && character <= '9')
                       || ('A' <= character && character <= 'F')
                   ))
            || bytes < 1048576L
            || bytes > 1073741824L
            || bytes &&& (bytes - 1L) <> 0L
            || length <= bytes
            || length > 1099511627776L
        then
            invalidOp "Fenced WAL object is invalid."

        {
            ObjectId = uuid "objectId" root
            Cluster = cluster
            RelativePath = path
            CiphertextSha256 = digest "ciphertextSha256" root
            CiphertextBytes = length
            Segment = name
            SegmentBytes = int bytes
        }

    let private requireFormat root =
        if
            not (DatabaseRestoreCanonical.exactProperties fields root)
            || text "format" root <> "claimcore-fenced-recovery-tail-1"
            || DatabaseRestoreCanonical.flag "realDataReady" root
            || not (DatabaseRestoreCanonical.flag "recoveryTailSealed" root)
        then
            invalidOp "Fenced tail format is invalid."

    let private authority root now =
        requireFormat root

        let scope = text "scope" root
        let checkedAt = instant "checkedAt" root
        let validUntil = instant "validUntil" root
        let epoch = DatabaseRestoreCanonical.number "epoch" root
        let oldGeneration = DatabaseRestoreCanonical.number "oldGeneration" root
        let newGeneration = DatabaseRestoreCanonical.number "newGeneration" root
        let w1Sequence = DatabaseRestoreCanonical.number "w1Sequence" root
        let archiveRoot = text "archiveRoot" root

        if
            (scope <> "synthetic-only" && scope <> "full")
            || checkedAt > now
            || validUntil <= now
            || validUntil - checkedAt > TimeSpan.FromMinutes(15.)
            || epoch < 1L
            || oldGeneration < 1L
            || newGeneration <> oldGeneration + 1L
            || w1Sequence < 1L
            || archiveRoot.Length < 1
            || archiveRoot.Length > 4096
        then
            invalidOp "Fenced tail authority or expiry is invalid."

        scope, checkedAt, validUntil, epoch, oldGeneration, newGeneration, w1Sequence, archiveRoot

    let private objects (root: JsonElement) =
        let source = root.GetProperty("walObjects")

        if
            source.ValueKind <> JsonValueKind.Array
            || source.GetArrayLength() < 2
            || source.GetArrayLength() > 2000
        then
            invalidOp "Fenced tail WAL object count is invalid."

        let walObjects = source.EnumerateArray() |> Seq.map walObject |> Seq.toList

        if
            (walObjects |> List.map _.ObjectId |> List.distinct |> List.length)
            <> walObjects.Length
            || (walObjects |> List.map _.RelativePath |> List.distinct |> List.length)
               <> walObjects.Length
        then
            invalidOp "Fenced tail contains duplicate archive identities."

        walObjects

    let private decoded (root: JsonElement) now =
        let (scope,
             checkedAt,
             validUntil,
             epoch,
             oldGeneration,
             newGeneration,
             w1Sequence,
             archiveRoot) =
            authority root now

        let walObjects = objects root

        {
            Scope = scope
            InstallationId = uuid "installationId" root
            LineageId = uuid "lineageId" root
            Epoch = epoch
            OldGeneration = oldGeneration
            NewGeneration = newGeneration
            HandoffId = uuid "handoffId" root
            W1Sequence = w1Sequence
            W1Hash = digest "w1Hash" root
            ReportSha256 = digest "reportSha256" root
            FenceReportSha256 = digest "fenceReportSha256" root
            CheckpointSignerKeyId = uuid "checkpointSignerKeyId" root
            PrimaryRegisteredWalHorizon = text "primaryRegisteredWalHorizon" root
            WitnessRegisteredWalHorizon = text "witnessRegisteredWalHorizon" root
            PrimaryFinalWalEndpoint = text "primaryFinalWalEndpoint" root
            WitnessFinalWalEndpoint = text "witnessFinalWalEndpoint" root
            ArchiveRoot = archiveRoot
            WalObjects = walObjects
            CheckedAt = checkedAt
            ValidUntil = validUntil
        }

    let parse (bytes: byte array) now =
        if bytes.Length < 2 || bytes.Length > 8388608 then
            None
        else
            match DatabaseRestoreCanonical.parse bytes with
            | None -> None
            | Some document ->
                use document = document

                try
                    Some(decoded document.RootElement now)
                with _ ->
                    None
