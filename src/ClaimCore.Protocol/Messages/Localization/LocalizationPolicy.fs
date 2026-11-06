namespace ClaimCore.Protocol

open System

/// Language and display preferences are independent, finite consumer capabilities.
module LocalizationPolicy =
    let languageOptions =
        [
            {|
                value = "en"
                label = "English"
                direction = "ltr"
                displayLocale = "en-GB"
                datePattern = "d MMM yyyy"
            |}
            {|
                value = "lv"
                label = "Latviešu"
                direction = "ltr"
                displayLocale = "lv-LV"
                datePattern = "d MMM yyyy"
            |}
            {|
                value = "ar"
                label = "العربية"
                direction = "rtl"
                displayLocale = "ar-AE"
                datePattern = "d MMM yyyy"
            |}
            {|
                value = "ko"
                label = "한국어"
                direction = "ltr"
                displayLocale = "ko-KR"
                datePattern = "yyyy년 M월 d일"
            |}
            {|
                value = "zh-Hans"
                label = "简体中文"
                direction = "ltr"
                displayLocale = "zh-CN"
                datePattern = "yyyy年M月d日"
            |}
            {|
                value = "zh-Hant"
                label = "繁體中文"
                direction = "ltr"
                displayLocale = "zh-TW"
                datePattern = "yyyy年M月d日"
            |}
            {|
                value = "ja"
                label = "日本語"
                direction = "ltr"
                displayLocale = "ja-JP"
                datePattern = "yyyy年M月d日"
            |}
            {|
                value = "es"
                label = "Español"
                direction = "ltr"
                displayLocale = "es-ES"
                datePattern = "d 'de' MMM 'de' yyyy"
            |}
            {|
                value = "de"
                label = "Deutsch"
                direction = "ltr"
                displayLocale = "de-DE"
                datePattern = "d. MMM yyyy"
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
