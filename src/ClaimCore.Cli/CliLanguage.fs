namespace ClaimCore.Cli

open System
open ClaimCore.Protocol

/// Presentation selection never changes authored requests or structured reply bytes.
module internal CliLanguage =
    let private supported value =
        List.contains value LocalizationPolicy.languages

    let locale (read: string -> string option) =
        [ "LC_ALL"; "LC_MESSAGES"; "LANG" ]
        |> List.tryPick (fun name ->
            read name
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.map (fun value -> name, value))
        |> Option.bind (fun (name, value: string) ->
            let language =
                value.Split([| '.'; '@'; '_'; '-' |])
                |> Array.head
                |> fun value -> value.ToLowerInvariant()

            if supported language then Some(language, name) else None)
        |> Option.defaultValue ("en", "default")

    let choose read profile =
        match read "CLAIMCORE_LANGUAGE" with
        | Some value when supported value -> value, "environment"
        | Some _ -> "en", "invalid-environment"
        | None ->
            match Map.tryFind CliSetting.Language profile with
            | Some value when supported value -> value, "profile"
            | _ -> locale read

    let resolveWith read =
        let profile =
            CliProfile.location read
            |> Result.bind CliProfile.read
            |> Result.defaultValue Map.empty

        choose read profile

    let environment name =
        Environment.GetEnvironmentVariable name |> Option.ofObj

    let effective () = resolveWith environment |> fst
