namespace ClaimCore.Protocol


/// Language and display preferences are independent, finite consumer capabilities.
module LocalizationPolicy =
    let languageOptions =
        [
            {|
                value = "en"
                label = "English"
                direction = "ltr"
            |}
            {|
                value = "lv"
                label = "Latviešu"
                direction = "ltr"
            |}
            {|
                value = "ar"
                label = "العربية"
                direction = "rtl"
            |}
        ]

    let languages = languageOptions |> List.map _.value
    let pseudolanguage = "en-XA"

    let displayOptions =
        [
            {|
                value = "en-GB"
                language = "en"
                label = "English — United Kingdom"
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
        ]

    let displayLocales = displayOptions |> List.map _.value
    let defaultLanguage = "en"
    let defaultDisplayLocale = "en-GB"
