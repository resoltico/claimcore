namespace ClaimCore.Database

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open System.Text.Json

[<NoEquality; NoComparison>]
type internal RestoreArchiveObject =
    {
        ObjectId: Guid
        CopyId: Guid
        Cluster: string
        Kind: string
        RelativePath: string
        Sha256: string
        Bytes: int64
        WalSegment: string option
        WalSegmentBytes: int option
    }

/// The signed evidence index names every encrypted BASE/WAL byte object used by the restore.
module internal DatabaseRestoreArchiveObjects =
    let private names =
        [
            "objectId"
            "copyId"
            "cluster"
            "kind"
            "relativePath"
            "sha256"
            "bytes"
            "walSegment"
            "walSegmentBytes"
        ]

    let private text name (root: JsonElement) =
        DatabaseRestoreCanonical.text name root
        |> Option.ofObj
        |> Option.defaultValue ""

    let private uuid name root =
        let raw = text name root

        match Guid.TryParseExact(raw, "D") with
        | true, value when value <> Guid.Empty && value.ToString("D") = raw -> value
        | _ -> invalidOp "Archive object identity is invalid."

    let private sha (value: string) =
        value.Length = 64
        && value
           |> Seq.forall (fun character ->
               ('0' <= character && character <= '9') || ('a' <= character && character <= 'f'))

    let private relative (value: string) =
        value.Length >= 1
        && value.Length <= 512
        && not (value.StartsWith("/", StringComparison.Ordinal))
        && not (value.EndsWith("/", StringComparison.Ordinal))
        && value.Split('/')
           |> Array.forall (fun segment ->
               segment.Length >= 1
               && segment <> "."
               && segment <> ".."
               && segment
                  |> Seq.forall (fun character ->
                      ('a' <= character && character <= 'z')
                      || ('A' <= character && character <= 'Z')
                      || ('0' <= character && character <= '9')
                      || character = '-'
                      || character = '_'
                      || character = '.'))

    let private walIdentity kind (root: JsonElement) =
        let segment = root.GetProperty("walSegment")
        let segmentBytes = root.GetProperty("walSegmentBytes")

        if kind = "WAL" then
            let name = segment.GetString() |> Option.ofObj |> Option.defaultValue ""
            let bytes = segmentBytes.GetInt32()

            if
                name.Length <> 24
                || name
                   |> Seq.exists (fun digit ->
                       not (('0' <= digit && digit <= '9') || ('A' <= digit && digit <= 'F')))
                || bytes < 1048576
                || bytes > 1073741824
                || bytes &&& (bytes - 1) <> 0
            then
                invalidOp "Archive WAL segment identity is invalid."

            Some name, Some bytes
        elif
            segment.ValueKind = JsonValueKind.Null
            && segmentBytes.ValueKind = JsonValueKind.Null
        then
            None, None
        else
            invalidOp "Base archive cannot claim a WAL segment."

    let private parseOne (root: JsonElement) =
        if not (DatabaseRestoreCanonical.exactProperties names root) then
            invalidOp "Archive object fields are invalid."

        let cluster = text "cluster" root
        let kind = text "kind" root
        let path = text "relativePath" root
        let digest = text "sha256" root
        let length = DatabaseRestoreCanonical.number "bytes" root
        let wal = walIdentity kind root

        if
            (cluster <> "PRIMARY" && cluster <> "WITNESS")
            || (kind <> "BASE" && kind <> "WAL")
            || not (relative path && sha digest)
            || length < 1L
            || length > 1099511627776L
        then
            invalidOp "Archive object metadata is invalid."

        {
            ObjectId = uuid "objectId" root
            CopyId = uuid "copyId" root
            Cluster = cluster
            Kind = kind
            RelativePath = path
            Sha256 = digest
            Bytes = length
            WalSegment = fst wal
            WalSegmentBytes = snd wal
        }

    let private distinct selector (values: RestoreArchiveObject list) =
        let selected = values |> List.map selector
        selected.Length = (selected |> Set.ofList |> Set.count)

    let validate (values: RestoreArchiveObject list) =
        if
            values.Length < 4
            || values.Length > 1000
            || not (distinct _.ObjectId values)
            || not (distinct _.CopyId values)
            || not (distinct _.RelativePath values)
            || (values |> List.map _.RelativePath)
               <> (values |> List.map _.RelativePath |> List.sort)
        then
            invalidOp "Archive object set is missing, duplicated, or unordered."

        for cluster in [ "PRIMARY"; "WITNESS" ] do
            for kind in [ "BASE"; "WAL" ] do
                if
                    not (
                        values
                        |> List.exists (fun value -> value.Cluster = cluster && value.Kind = kind)
                    )
                then
                    invalidOp "Archive object set lacks a required cluster or kind."

        values

    let parse (items: JsonElement) =
        if items.ValueKind <> JsonValueKind.Array then
            invalidOp "Archive object set is not an array."

        [ for item in items.EnumerateArray() -> parseOne item ] |> validate

    let rootDigest values =
        let verified = validate values
        let builder = StringBuilder("claimcore:restore-archive-objects:v1\n")

        for value in verified do
            builder
                .Append(value.ObjectId.ToString("D"))
                .Append('|')
                .Append(value.CopyId.ToString("D"))
                .Append('|')
                .Append(value.Cluster)
                .Append('|')
                .Append(value.Kind)
                .Append('|')
                .Append(value.RelativePath)
                .Append('|')
                .Append(value.Sha256)
                .Append('|')
                .Append(value.Bytes.ToString(CultureInfo.InvariantCulture))
                .Append('|')
                .Append(value.WalSegment |> Option.defaultValue "-")
                .Append('|')
                .Append(
                    value.WalSegmentBytes
                    |> Option.map (fun bytes -> bytes.ToString(CultureInfo.InvariantCulture))
                    |> Option.defaultValue "-"
                )
                .Append('\n')
            |> ignore

        let bytes = Encoding.ASCII.GetBytes(builder.ToString())

        try
            SHA256.HashData(bytes) |> Convert.ToHexStringLower
        finally
            CryptographicOperations.ZeroMemory(bytes)

    let totalBytes values = validate values |> List.sumBy _.Bytes
