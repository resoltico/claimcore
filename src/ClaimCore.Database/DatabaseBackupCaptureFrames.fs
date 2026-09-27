namespace ClaimCore.Database

open System
open System.Globalization
open System.IO
open System.Text.Json

[<NoEquality; NoComparison>]
type internal BackupCaptureFiles =
    {
        PrimaryCiphertextPath: string
        WitnessCiphertextPath: string
        CheckpointPath: string
        CycleManifestPath: string
        CycleManifestSignaturePath: string
    }

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type internal BackupCaptureFrame =
    | Begin of nonce: string * expiresAt: DateTimeOffset
    | Finish of nonce: string * leaseId: Guid * files: BackupCaptureFiles
    | Abort of nonce: string * leaseId: Guid
    | Observe of nonce: string * cycleReceiptId: Guid * receiptSha256: string

/// Exact private-pipe frames; paths are admitted under owner-held directories later.
module internal DatabaseBackupCaptureFrames =
    let private format = "claimcore-backup-barrier-frame-1"

    let private exact names (value: JsonElement) =
        if not (DatabaseRestoreCanonical.exactProperties names value) then
            invalidOp "Backup capture frame has an unexpected field."

    let private text (name: string) (value: JsonElement) =
        match value.GetProperty(name).GetString() with
        | null -> invalidOp "Backup capture frame has a missing value."
        | item -> item

    let private guid (name: string) (value: JsonElement) =
        let encoded = text name value
        let parsed = Guid.ParseExact(encoded, "D")

        if parsed = Guid.Empty || encoded <> parsed.ToString("D") then
            invalidOp "Backup capture frame identity is invalid."

        parsed

    let private digest (value: string) =
        value.Length = 64
        && (value |> Seq.forall (fun c -> ('0' <= c && c <= '9') || ('a' <= c && c <= 'f')))

    let private nonce (value: JsonElement) =
        let item = text "nonce" value

        if not (digest item) then
            invalidOp "Backup capture nonce is invalid."

        item

    let private file (name: string) (value: JsonElement) =
        let path = text name value

        if
            not (Path.IsPathFullyQualified path)
            || path.Length > 4096
            || (path.Split([| '/'; '\\' |], StringSplitOptions.None)
                |> Array.exists (fun part -> part = ".." || part = "."))
        then
            invalidOp "Backup capture private path is invalid."

        path

    let private beginFrame value now identity =
        exact [ "format"; "kind"; "nonce"; "expiresAt" ] value
        let encoded = text "expiresAt" value

        let expires =
            DateTimeOffset.ParseExact(
                encoded,
                "yyyy-MM-ddTHH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )

        if
            expires <= now
            || expires > now.AddMinutes(30.)
            || expires.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)
               <> encoded
        then
            invalidOp "Backup capture lease expiry is invalid."

        BackupCaptureFrame.Begin(identity, expires)

    let private finishFrame value identity =
        exact
            [
                "format"
                "kind"
                "nonce"
                "leaseId"
                "primaryCiphertextPath"
                "witnessCiphertextPath"
                "checkpointPath"
                "cycleManifestPath"
                "cycleManifestSignaturePath"
            ]
            value

        let paths =
            {
                PrimaryCiphertextPath = file "primaryCiphertextPath" value
                WitnessCiphertextPath = file "witnessCiphertextPath" value
                CheckpointPath = file "checkpointPath" value
                CycleManifestPath = file "cycleManifestPath" value
                CycleManifestSignaturePath = file "cycleManifestSignaturePath" value
            }

        let names =
            [
                paths.PrimaryCiphertextPath
                paths.WitnessCiphertextPath
                paths.CheckpointPath
                paths.CycleManifestPath
                paths.CycleManifestSignaturePath
            ]

        if (names |> Set.ofList |> Set.count) <> names.Length then
            invalidOp "Backup capture file identities overlap."

        BackupCaptureFrame.Finish(identity, guid "leaseId" value, paths)

    let parse (bytes: byte array) (now: DateTimeOffset) =
        use document =
            if isNull (box bytes) || bytes.Length > 16384 then
                invalidOp "Backup capture frame exceeds its bound."

            DatabaseRestoreCanonical.parse bytes
            |> Option.defaultWith (fun () -> invalidOp "Backup capture frame is noncanonical.")

        let value = document.RootElement

        if text "format" value <> format then
            invalidOp "Backup capture frame format is unsupported."

        let tag = text "kind" value
        let identity = nonce value

        match tag with
        | "BEGIN" -> beginFrame value now identity
        | "FINISH" -> finishFrame value identity
        | "ABORT" ->
            exact [ "format"; "kind"; "nonce"; "leaseId" ] value
            BackupCaptureFrame.Abort(identity, guid "leaseId" value)
        | "OBSERVE" ->
            exact [ "format"; "kind"; "nonce"; "cycleReceiptId"; "receiptSha256" ] value
            let receipt = text "receiptSha256" value

            if not (digest receipt) then
                invalidOp "Backup capture receipt digest is invalid."

            BackupCaptureFrame.Observe(identity, guid "cycleReceiptId" value, receipt)
        | _ -> invalidOp "Backup capture frame kind is unsupported."
