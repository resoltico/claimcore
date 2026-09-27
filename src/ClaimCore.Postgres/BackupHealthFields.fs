namespace ClaimCore.Postgres

open System
open System.Globalization
open System.Text.Json
open System.Text.RegularExpressions

module internal BackupHealthFields =
    let text (root: JsonElement) (name: string) =
        let value = root.GetProperty(name)

        if value.ValueKind <> JsonValueKind.String then
            invalidOp "Backup health text is invalid."

        value.GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Backup health text is absent.")

    let number (root: JsonElement) (name: string) =
        let value = root.GetProperty(name)

        if value.ValueKind <> JsonValueKind.Number then
            invalidOp "Backup health number is invalid."

        value.GetInt64()

    let optionalNumber (root: JsonElement) (name: string) =
        let value = root.GetProperty(name)

        match value.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.Number -> Some(value.GetInt64())
        | _ -> invalidOp "Backup health optional number is invalid."

    let optionalText (root: JsonElement) (name: string) =
        let value = root.GetProperty(name)

        match value.ValueKind with
        | JsonValueKind.Null -> None
        | JsonValueKind.String -> Some(text root name)
        | _ -> invalidOp "Backup health optional text is invalid."

    let uuid (root: JsonElement) (name: string) =
        let raw = text root name
        let value = Guid.ParseExact(raw, "D")

        if value = Guid.Empty || value.ToString("D") <> raw then
            invalidOp "Backup health identity is noncanonical."

        value

    let optionalUuid (root: JsonElement) (name: string) =
        optionalText root name
        |> Option.map (fun raw ->
            let value = Guid.ParseExact(raw, "D")

            if value = Guid.Empty || value.ToString("D") <> raw then
                invalidOp "Backup health identity is noncanonical."

            value)

    let sha (root: JsonElement) (name: string) =
        let raw = text root name

        if not (Regex.IsMatch(raw, "^[0-9a-f]{64}$")) then
            invalidOp "Backup health digest is invalid."

        raw

    let optionalSha (root: JsonElement) (name: string) =
        optionalText root name |> Option.map (fun _ -> sha root name)

    let lsn (root: JsonElement) (name: string) =
        let raw = text root name

        if not (Regex.IsMatch(raw, "^[0-9A-F]{1,8}/[0-9A-F]{1,8}$")) then
            invalidOp "Backup health WAL horizon is invalid."

        raw

    let instant (root: JsonElement) (name: string) =
        let raw = text root name

        let value =
            DateTimeOffset.ParseExact(
                raw,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            )

        if value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) <> raw then
            invalidOp "Backup health time is noncanonical."

        value

    let positive (maximum: int64) (value: int64) =
        if value < 1L || value > maximum then
            invalidOp "Backup health policy bound is invalid."

        value

    let policyId (root: JsonElement) =
        let raw = text root "policyId"

        if not (Regex.IsMatch(raw, "^[A-Za-z0-9][A-Za-z0-9_.:-]{0,79}$")) then
            invalidOp "Backup health policy identity is invalid."

        raw

    let systemId (root: JsonElement) (name: string) =
        let raw = text root name

        if not (Regex.IsMatch(raw, "^[0-9]{1,20}$")) then
            invalidOp "Backup health PostgreSQL system identity is invalid."

        raw
