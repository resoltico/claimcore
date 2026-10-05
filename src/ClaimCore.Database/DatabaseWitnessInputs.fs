namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Text.Json
open ClaimCore.Application
open ClaimCore.HostSecurity
open ClaimCore.Witness

/// Owner-private administration inputs. Neither parsed identities nor key bytes are diagnostics.
module internal DatabaseWitnessInputs =
    let private privateBytes setting missing refused =
        match Environment.GetEnvironmentVariable(setting) |> Option.ofObj with
        | None -> Error missing
        | Some path ->
            match PrivateFileService.readBinary 32768 path with
            | Ok bytes -> Ok bytes
            | Error _ -> Error refused

    let private exact names (element: JsonElement) =
        element.ValueKind = JsonValueKind.Object
        && (element.EnumerateObject() |> Seq.length) = List.length names
        && (element.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq) = Set.ofList names

    let private readJson setting missing refused invalid parse =
        privateBytes setting missing refused
        |> Result.bind (fun bytes ->
            try
                try
                    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
                    parse document.RootElement
                with _ ->
                    Error invalid
            finally
                CryptographicOperations.ZeroMemory(bytes))

    let private privateText setting =
        privateBytes
            setting
            DatabaseInputProblem.WitnessSettingMissing
            DatabaseInputProblem.WitnessFileRefused
        |> Result.bind (fun bytes ->
            try
                try
                    let value = Text.UTF8Encoding(false, true).GetString(bytes).Trim()

                    if String.IsNullOrWhiteSpace value then
                        Error DatabaseInputProblem.WitnessFileInvalid
                    else
                        Ok(PostgresTransport.connectionString value)
                with _ ->
                    Error DatabaseInputProblem.WitnessFileInvalid
            finally
                CryptographicOperations.ZeroMemory(bytes))

    let witnessOwnerConnection () =
        privateText "CLAIMCORE_WITNESS_ADMIN_CONNECTION_FILE"

    let witnessWriterConnection () =
        privateText "CLAIMCORE_WITNESS_CONNECTION_FILE"

    let witnessAuditConnection () =
        privateText "CLAIMCORE_WITNESS_AUDIT_CONNECTION_FILE"

    let writerCapability () =
        match Environment.GetEnvironmentVariable("CLAIMCORE_WRITER_CAPABILITY_FILE") with
        | null
        | "" -> Error DatabaseInputProblem.WitnessSettingMissing
        | path ->
            try
                Ok(WriterCapabilityFile.Load(path))
            with _ ->
                Error DatabaseInputProblem.WitnessFileRefused

    let keyRing () =
        privateBytes
            "CLAIMCORE_WITNESS_KEY_FILE"
            DatabaseInputProblem.WitnessSettingMissing
            DatabaseInputProblem.WitnessFileRefused
        |> Result.bind (fun bytes ->
            try
                try
                    Ok(KeyRingCodec.parse bytes)
                with _ ->
                    Error DatabaseInputProblem.WitnessFileInvalid
            finally
                CryptographicOperations.ZeroMemory(bytes))

    let initialOwner () =
        readJson
            "CLAIMCORE_INITIAL_OWNER_PRINCIPAL_FILE"
            DatabaseInputProblem.PrincipalFileRefused
            DatabaseInputProblem.PrincipalFileRefused
            DatabaseInputProblem.PrincipalFileInvalid
            (fun root ->
                if not (exact [ "issuer"; "subject" ] root) then
                    Error DatabaseInputProblem.PrincipalFileInvalid
                else
                    let issuer =
                        root.GetProperty("issuer").GetString()
                        |> Option.ofObj
                        |> Option.defaultWith (fun () -> invalidOp "Invalid private principal.")

                    let subject =
                        root.GetProperty("subject").GetString()
                        |> Option.ofObj
                        |> Option.defaultWith (fun () -> invalidOp "Invalid private principal.")

                    match PrincipalKey.human issuer subject with
                    | Ok principal -> Ok principal
                    | Error _ -> Error DatabaseInputProblem.PrincipalFileInvalid)

    let suppressionKey () =
        match Environment.GetEnvironmentVariable("CLAIMCORE_SUPPRESSION_KEY_FILE") with
        | null
        | "" -> Error DatabaseInputProblem.SuppressionKeyFileRefused
        | path ->
            try
                Ok(SuppressionKeyFile.Load(path))
            with _ ->
                Error DatabaseInputProblem.SuppressionKeyFileRefused
