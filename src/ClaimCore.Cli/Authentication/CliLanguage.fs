namespace ClaimCore.Cli

open System
open ClaimCore.Wire
open ClaimCore.Presentation

/// Immutable presentation inputs. Machine contracts never consume this context.
[<StructuralEquality; StructuralComparison>]
type RenderingContext =
    private
        {
            LanguageValue: string
            DisplayLocaleValue: string
        }

    member this.Language = this.LanguageValue
    member this.DisplayLocale = this.DisplayLocaleValue

module RenderingContext =
    let create language displayLocale =
        if
            List.contains language LocalizationPolicy.languages
            && List.contains displayLocale LocalizationPolicy.displayLocales
        then
            Some
                {
                    LanguageValue = language
                    DisplayLocaleValue = displayLocale
                }
        else
            None

    let forLanguage language =
        let option = LocalizationPolicy.languageOption language
        create language option.displayLocale |> Option.get

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
                value.Split([| '.'; '@' |])
                |> Array.head
                |> fun value -> value.Replace('_', '-')

            LocalizationPolicy.tryLanguage language
            |> Option.map (fun language -> language, name))
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

    let displayLocale read profile language supplied =
        let selected =
            match supplied with
            | Some value -> value, "flag"
            | None ->
                match read "CLAIMCORE_DISPLAY_LOCALE" with
                | Some value -> value, "environment"
                | None ->
                    match Map.tryFind CliSetting.DisplayLocale profile with
                    | Some value -> value, "profile"
                    | None -> (LocalizationPolicy.languageOption language).displayLocale, "language"

        RenderingContext.create language (fst selected)
        |> Option.map (fun context -> context, snd selected)
        |> Option.map Ok
        |> Option.defaultValue (Error(fst selected))
