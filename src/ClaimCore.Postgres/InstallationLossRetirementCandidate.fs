namespace ClaimCore.Postgres

open System
open System.Collections.Generic
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
type internal InstallationLossOperationSet =
    | Known
    | Unknown

[<NoEquality; NoComparison>]
type internal InstallationLossRetirementDecision =
    {
        RetirementId: Guid
        InstallationId: Guid
        LineageId: Guid
        Epoch: int64
        PreviousSequence: int64
        PreviousHash: byte array
        EvidenceReportSha256: byte array option
        IndependentCheckpointSha256: byte array option
        OperationSet: InstallationLossOperationSet
        KnownOperationCount: int
        KnownOperationDigest: byte array
        SignerOneId: Guid
        SignerTwoId: Guid
        OwnerOneActorId: Guid
        OwnerTwoActorId: Guid
        OwnerOneGrantRevision: int64
        OwnerTwoGrantRevision: int64
        AuthorityRevision: int64
        ValidUntil: DateTimeOffset
    }

module internal InstallationLossRetirementCandidate =
    let operationSetName =
        function
        | InstallationLossOperationSet.Known -> "KNOWN_OPERATIONS"
        | InstallationLossOperationSet.Unknown -> "UNKNOWN_OPERATIONS"

    let private operationSet =
        function
        | "KNOWN_OPERATIONS" -> InstallationLossOperationSet.Known
        | "UNKNOWN_OPERATIONS" -> InstallationLossOperationSet.Unknown
        | _ -> invalidOp "Loss operation-set mode is invalid."

    let private hex (value: byte array) = Convert.ToHexStringLower(value)
    let private uuid (value: Guid) = value.ToString("D")

    let private time (value: DateTimeOffset) =
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)

    let private boundedDigest (source: byte array option) =
        match source with
        | None -> null
        | Some value when value.Length = 32 -> hex value
        | Some _ -> invalidOp "Loss evidence digest is invalid."

    let encode (value: InstallationLossRetirementDecision) =
        let fields = SortedDictionary<string, objnull>(StringComparer.Ordinal)
        let put name item = fields.Add(name, box item)
        put "format" "claimcore-installation-loss-retirement-1"
        put "retirementId" (uuid value.RetirementId)
        put "installationId" (uuid value.InstallationId)
        put "lineageId" (uuid value.LineageId)
        put "epoch" value.Epoch
        put "previousSequence" value.PreviousSequence
        put "previousHash" (hex value.PreviousHash)

        put
            "evidenceReportState"
            (if value.EvidenceReportSha256.IsSome then
                 "PRESENT"
             else
                 "MISSING")

        put "evidenceReportSha256" (boundedDigest value.EvidenceReportSha256)

        put
            "checkpointState"
            (if value.IndependentCheckpointSha256.IsSome then
                 "PRESENT"
             else
                 "MISSING")

        put "independentCheckpointSha256" (boundedDigest value.IndependentCheckpointSha256)
        put "operationSet" (operationSetName value.OperationSet)
        put "knownOperationCount" value.KnownOperationCount
        put "knownOperationDigest" (hex value.KnownOperationDigest)
        put "signerOneId" (uuid value.SignerOneId)
        put "signerTwoId" (uuid value.SignerTwoId)
        put "ownerOneActorId" (uuid value.OwnerOneActorId)
        put "ownerTwoActorId" (uuid value.OwnerTwoActorId)
        put "ownerOneGrantRevision" value.OwnerOneGrantRevision
        put "ownerTwoGrantRevision" value.OwnerTwoGrantRevision
        put "authorityRevision" value.AuthorityRevision
        put "validUntil" (time value.ValidUntil)
        Encoding.ASCII.GetBytes(JsonSerializer.Serialize(fields) + "\n")

    let private names =
        set
            [
                "format"
                "retirementId"
                "installationId"
                "lineageId"
                "epoch"
                "previousSequence"
                "previousHash"
                "evidenceReportState"
                "evidenceReportSha256"
                "checkpointState"
                "independentCheckpointSha256"
                "operationSet"
                "knownOperationCount"
                "knownOperationDigest"
                "signerOneId"
                "signerTwoId"
                "ownerOneActorId"
                "ownerTwoActorId"
                "ownerOneGrantRevision"
                "ownerTwoGrantRevision"
                "authorityRevision"
                "validUntil"
            ]

    let private text (root: JsonElement) (name: string) =
        root.GetProperty(name).GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Loss candidate string is unavailable.")

    let private id (root: JsonElement) (name: string) =
        let raw = text root name
        let value = Guid.ParseExact(raw, "D")

        if value = Guid.Empty || value.ToString("D") <> raw then
            invalidOp "Loss identity is invalid."

        value

    let private digest (root: JsonElement) (name: string) =
        let raw = text root name

        if
            raw.Length <> 64
            || (raw |> Seq.exists (fun c -> not (Char.IsAsciiHexDigitLower c)))
        then
            invalidOp "Loss digest is invalid."

        Convert.FromHexString(raw)

    let private optionalDigest (root: JsonElement) (stateName: string) (digestName: string) =
        let state = text root stateName
        let element = root.GetProperty(digestName)

        match state, element.ValueKind with
        | "MISSING", JsonValueKind.Null -> None
        | "PRESENT", JsonValueKind.String -> Some(digest root digestName)
        | _ -> invalidOp "Loss evidence state is invalid."

    let private parseTime (root: JsonElement) =
        DateTimeOffset.ParseExact(
            text root "validUntil",
            "yyyy-MM-ddTHH:mm:ss'Z'",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
        )

    let private decode (root: JsonElement) =
        {
            RetirementId = id root "retirementId"
            InstallationId = id root "installationId"
            LineageId = id root "lineageId"
            Epoch = root.GetProperty("epoch").GetInt64()
            PreviousSequence = root.GetProperty("previousSequence").GetInt64()
            PreviousHash = digest root "previousHash"
            EvidenceReportSha256 = optionalDigest root "evidenceReportState" "evidenceReportSha256"
            IndependentCheckpointSha256 =
                optionalDigest root "checkpointState" "independentCheckpointSha256"
            OperationSet = operationSet (text root "operationSet")
            KnownOperationCount = root.GetProperty("knownOperationCount").GetInt32()
            KnownOperationDigest = digest root "knownOperationDigest"
            SignerOneId = id root "signerOneId"
            SignerTwoId = id root "signerTwoId"
            OwnerOneActorId = id root "ownerOneActorId"
            OwnerTwoActorId = id root "ownerTwoActorId"
            OwnerOneGrantRevision = root.GetProperty("ownerOneGrantRevision").GetInt64()
            OwnerTwoGrantRevision = root.GetProperty("ownerTwoGrantRevision").GetInt64()
            AuthorityRevision = root.GetProperty("authorityRevision").GetInt64()
            ValidUntil = parseTime root
        }

    let private valid (value: InstallationLossRetirementDecision) canonical =
        value.Epoch > 0L
        && value.PreviousSequence >= 0L
        && value.KnownOperationCount >= 0
        && value.KnownOperationCount <= 10000
        && value.OwnerOneGrantRevision > 0L
        && value.OwnerTwoGrantRevision > 0L
        && value.AuthorityRevision > 0L
        && value.SignerOneId <> value.SignerTwoId
        && value.OwnerOneActorId <> value.OwnerTwoActorId
        && (value.OperationSet <> InstallationLossOperationSet.Unknown
            || value.KnownOperationCount = 0)
        && encode value = canonical

    let parse (canonical: byte array) =
        if isNull (box canonical) || canonical.Length < 2 || canonical.Length > 16384 then
            None
        else
            try
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(canonical))
                let root = document.RootElement

                if
                    root.ValueKind <> JsonValueKind.Object
                    || (root.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) <> names
                    || (root.EnumerateObject() |> Seq.length) <> names.Count
                    || text root "format" <> "claimcore-installation-loss-retirement-1"
                then
                    None
                else
                    let value = decode root
                    if valid value canonical then Some value else None
            with _ ->
                None

    let evidenceDigest (source: byte array option) =
        match source with
        | None -> None
        | Some bytes when isNull (box bytes) || bytes.Length < 1 || bytes.Length > 4194304 ->
            invalidOp "Loss evidence file bounds are invalid."
        | Some bytes -> Some(SHA256.HashData(bytes))
