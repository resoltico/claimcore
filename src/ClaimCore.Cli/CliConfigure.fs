namespace ClaimCore.Cli

open System
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Json

module internal CliConfigure =
    let private names =
        CliProfile.fields
        |> List.map (fun (setting, name) -> "--" + CommandGrammar.kebab name, setting)
        |> Map.ofList

    let private value flag text =
        Map.tryFind flag names
        |> Option.map (fun setting -> Ok(setting, text))
        |> Option.defaultValue (Error(UsageError.UnknownOption(flag, None)))

    let private collect arguments =
        let rec loop values =
            function
            | [] -> Ok values
            | flag :: text :: tail ->
                value flag text
                |> Result.bind (fun (setting, text) ->
                    if Map.containsKey setting values then
                        Error(UsageError.DuplicateOption flag)
                    else
                        loop (Map.add setting text values) tail)
            | [ flag ] -> Error(UsageError.MissingValue flag)

        loop Map.empty arguments

    let private shown path (values: Map<CliSetting, string option * string>) =
        let fields =
            InputDiagnostics.settings
            |> List.map (fun (setting, name) ->
                let value, source = values |> Map.find setting

                let settingValue =
                    if setting = CliSetting.SecretFile then
                        "present", JsonValue.Boolean value.IsSome
                    else
                        "value",
                        value |> Option.map JsonValue.String |> Option.defaultValue JsonValue.Null

                name, JsonValue.Object [ settingValue; "source", JsonValue.String source ])

        JsonValue.Object
            [ "profileFile", JsonValue.String path; "settings", JsonValue.Object fields ]
        |> WireWriter.indentedBytes

    let showWith environment =
        RemoteConfiguration.sources environment
        |> Result.map (fun (path, values) -> shown path values)

    let show () =
        showWith RemoteConfiguration.environment

    let save updates =
        CliProfile.location RemoteConfiguration.environment
        |> Result.bind (fun path ->
            CliProfile.read path
            |> Result.bind (fun existing ->
                let values =
                    Map.fold (fun values key value -> Map.add key value values) existing updates

                RemoteConfiguration.validatePublic values
                |> Result.bind (fun () -> CliProfile.write path values)))

    let admit arguments =
        match arguments with
        | [ "--show" ] -> Ok None
        | [] -> Error(UsageError.MissingOption "--service-url")
        | _ -> collect arguments |> Result.map Some
