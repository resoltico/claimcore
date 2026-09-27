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
                        Ok value
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
        readJson
            "CLAIMCORE_WITNESS_KEY_FILE"
            DatabaseInputProblem.WitnessSettingMissing
            DatabaseInputProblem.WitnessFileRefused
            DatabaseInputProblem.WitnessFileInvalid
            (fun root ->
                if
                    not (exact [ "version"; "activeKeyId"; "keys" ] root)
                    || root.GetProperty("version").GetInt32() <> 1
                then
                    Error DatabaseInputProblem.WitnessFileInvalid
                else
                    let active = root.GetProperty("activeKeyId").GetGuid()
                    let keys = root.GetProperty("keys")

                    if
                        keys.ValueKind <> JsonValueKind.Array
                        || keys.GetArrayLength() < 1
                        || keys.GetArrayLength() > 64
                    then
                        Error DatabaseInputProblem.WitnessFileInvalid
                    else
                        let decoded = ResizeArray<Guid * byte array>()

                        try
                            for entry in keys.EnumerateArray() do
                                if not (exact [ "id"; "materialBase64" ] entry) then
                                    invalidOp "Invalid private witness key ring."

                                let keyId = entry.GetProperty("id").GetGuid()

                                let encoded =
                                    entry.GetProperty("materialBase64").GetString()
                                    |> Option.ofObj
                                    |> Option.defaultWith (fun () ->
                                        invalidOp "Invalid private witness key ring.")

                                let material = Convert.FromBase64String(encoded)

                                if
                                    material.Length <> 32
                                    || Convert.ToBase64String(material) <> encoded
                                then
                                    CryptographicOperations.ZeroMemory(material)
                                    invalidOp "Invalid private witness key ring."

                                decoded.Add(keyId, material)

                            Ok(new KeyRing(active, decoded) :> IKeyCustody)
                        finally
                            for _, material in decoded do
                                CryptographicOperations.ZeroMemory(material))

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
