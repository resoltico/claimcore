namespace ClaimCore.Presentation

open System

/// Language and display preferences are independent, finite consumer capabilities.
module LocalizationPolicy =
    let languageOptions =
        [
            {|
                value = "en"
                required = true
                label = "English"
                direction = "ltr"
                displayLocale = "en-GB"
            |}
            {|
                value = "lv"
                required = true
                label = "Latviešu"
                direction = "ltr"
                displayLocale = "lv-LV"
            |}
            {|
                value = "ar"
                required = true
                label = "العربية"
                direction = "rtl"
                displayLocale = "ar-AE"
            |}
            {|
                value = "ko"
                required = false
                label = "한국어"
                direction = "ltr"
                displayLocale = "ko-KR"
            |}
            {|
                value = "zh-Hans"
                required = false
                label = "简体中文"
                direction = "ltr"
                displayLocale = "zh-CN"
            |}
            {|
                value = "zh-Hant"
                required = false
                label = "繁體中文"
                direction = "ltr"
                displayLocale = "zh-TW"
            |}
            {|
                value = "ja"
                required = false
                label = "日本語"
                direction = "ltr"
                displayLocale = "ja-JP"
            |}
            {|
                value = "es"
                required = false
                label = "Español"
                direction = "ltr"
                displayLocale = "es-ES"
            |}
            {|
                value = "de"
                required = false
                label = "Deutsch"
                direction = "ltr"
                displayLocale = "de-DE"
            |}
        ]

    let languages = languageOptions |> List.map _.value
    let pseudolanguage = "en-XA"

    // Written Chinese defaults follow CLDR; an explicit script takes precedence over region.
    let languageAliases =
        [
            "zh", "zh-Hans"
            "zh-CN", "zh-Hans"
            "zh-SG", "zh-Hans"
            "zh-TW", "zh-Hant"
            "zh-HK", "zh-Hant"
            "zh-MO", "zh-Hant"
        ]
        |> Map.ofList

    let tryLanguage (tag: string) =
        let choices =
            (languages |> List.map (fun language -> language, language))
            @ Map.toList languageAliases

        let rec lookup (candidate: string) =
            match
                choices
                |> List.tryFind (fun (range, _) ->
                    String.Equals(range, candidate, StringComparison.OrdinalIgnoreCase))
            with
            | Some(_, language) -> Some language
            | None ->
                let separator = candidate.LastIndexOf '-'

                if separator < 0 then
                    None
                else
                    lookup candidate[.. separator - 1]

        if String.IsNullOrWhiteSpace tag || tag.Length > 64 then
            None
        else
            lookup tag

    let languageOption language =
        languageOptions |> List.find (fun option -> option.value = language)

    let displayOptions =
        [
            {|
                value = "en-GB"
                language = "en"
                label = "English — United Kingdom"
            |}
            {|
                value = "en-US"
                language = "en"
                label = "English — United States"
            |}
            {|
                value = "lv-LV"
                language = "lv"
                label = "Latviešu — Latvija"
            |}
            {|
                value = "ar-EG"
                language = "ar"
                label = "العربية — مصر"
            |}
            {|
                value = "ar-AE"
                language = "ar"
                label = "العربية — الإمارات العربية المتحدة"
            |}
            {|
                value = "de-DE"
                language = "de"
                label = "Deutsch — Deutschland"
            |}
            {|
                value = "fr-FR"
                language = "fr"
                label = "Français — France"
            |}
            {|
                value = "ko-KR"
                language = "ko"
                label = "한국어 — 대한민국"
            |}
            {|
                value = "zh-CN"
                language = "zh-Hans"
                label = "中文 — 中国大陆"
            |}
            {|
                value = "zh-TW"
                language = "zh-Hant"
                label = "中文 — 台灣"
            |}
            {|
                value = "zh-HK"
                language = "zh-Hant"
                label = "中文 — 香港"
            |}
            {|
                value = "ja-JP"
                language = "ja"
                label = "日本語 — 日本"
            |}
            {|
                value = "es-ES"
                language = "es"
                label = "Español — España"
            |}
            {|
                value = "es-MX"
                language = "es"
                label = "Español — México"
            |}
            {|
                value = "es-AR"
                language = "es"
                label = "Español — Argentina"
            |}
            {|
                value = "de-AT"
                language = "de"
                label = "Deutsch — Österreich"
            |}
            {|
                value = "de-CH"
                language = "de"
                label = "Deutsch — Schweiz"
            |}
        ]

    let displayLocales = displayOptions |> List.map _.value
    let defaultLanguage = "en"
    let defaultDisplayLocale = "en-GB"

    let accents = [ "green"; "blue" ]
    let modes = [ "system"; "light"; "dark" ]

    type Translation = { Text: string; Source: string }

    type Issue =
        {
            Language: string
            Key: string
            Reason: string
        }

    /// Publication requires every offered language, independently of development fallback policy.
    let releaseReady (issues: Issue list) = List.isEmpty issues

    let sourceHash (text: string) =
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes text)
        |> Convert.ToHexStringLower
        |> fun value -> value[..11]

    /// English owns every key. A translation is current only for its exact English source.
    let resolve language (english: Map<string, string>) (translated: Map<string, Translation>) =
        for KeyValue(key, _) in translated do
            if not (Map.containsKey key english) then
                invalidOp ("Unknown translation key: " + language + " " + key)

        let entries =
            english
            |> Map.map (fun key original ->
                match Map.tryFind key translated with
                | Some entry when entry.Source = sourceHash original -> entry.Text
                | _ -> original)

        let issues =
            english
            |> Map.toList
            |> List.choose (fun (key, original) ->
                let reason =
                    match Map.tryFind key translated with
                    | None -> Some "missing"
                    | Some entry when entry.Source <> sourceHash original -> Some "stale"
                    | _ -> None

                reason
                |> Option.map (fun reason ->
                    {
                        Language = language
                        Key = key
                        Reason = reason
                    }))

        entries, issues

    let private fields owner bytes =
        match ClaimCore.Json.StrictJson.read Int32.MaxValue bytes with
        | Ok(ClaimCore.Json.JsonValue.Object fields) -> fields
        | _ -> invalidOp ("Invalid localization object: " + owner)

    let private unique owner pairs =
        let entries = List.collect id pairs
        let result = Map.ofList entries

        if result.Count <> entries.Length then
            invalidOp ("Duplicate localization key: " + owner)

        result

    /// Parse authored resources without filesystem, assembly or HTTP access.
    let load language englishSources translatedSources =
        if not (List.contains language languages) then
            invalidArg (nameof language) "Unknown localization language."

        let english =
            englishSources
            |> List.map (fun bytes ->
                fields "en" bytes
                |> List.map (fun (key, value) ->
                    match value with
                    | ClaimCore.Json.JsonValue.String text -> key, text
                    | _ -> invalidOp ("English localization requires text: " + key)))
            |> unique "en"

        if language = "en" then
            english, []
        else
            let translations =
                translatedSources
                |> List.map (fun bytes ->
                    fields language bytes
                    |> List.map (fun (key, value) ->
                        match value with
                        | ClaimCore.Json.JsonValue.Object members when members.Length = 2 ->
                            match Map.ofList members with
                            | properties when
                                properties.ContainsKey "text" && properties.ContainsKey "source"
                                ->
                                match properties["text"], properties["source"] with
                                | ClaimCore.Json.JsonValue.String text,
                                  ClaimCore.Json.JsonValue.String source when
                                    System.Text.RegularExpressions.Regex.IsMatch(
                                        source,
                                        @"^[0-9a-f]{12}\z",
                                        System.Text.RegularExpressions
                                            .RegexOptions.CultureInvariant
                                    )
                                    ->
                                    key, { Text = text; Source = source }
                                | _ ->
                                    invalidOp (
                                        "Invalid translation text or source: "
                                        + language
                                        + " "
                                        + key
                                    )
                            | _ ->
                                invalidOp (
                                    "Translation requires text and source: "
                                    + language
                                    + " "
                                    + key
                                )
                        | _ ->
                            invalidOp (
                                "Translation requires text and source: " + language + " " + key
                            )))
                |> unique language

            resolve language english translations
