namespace ClaimCore.Cli

open System
open System.Globalization
open System.Numerics
open System.Text
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain
open ClaimCore.Json

module internal ReplyTextFormat =

    // The CLI has language selection, not a separate display-locale preference.
    // Explicit regional cultures keep numeric conventions independent of neutral-culture OS defaults.
    let private displayCultures =
        LocalizationPolicy.languageOptions
        |> List.map (fun option ->
            let culture =
                CultureInfo.GetCultureInfo(option.displayLocale).Clone() :?> CultureInfo

            culture.DateTimeFormat.Calendar <- GregorianCalendar()
            option.value, CultureInfo.ReadOnly culture)
        |> Map.ofList

    let displayCulture language = Map.find language displayCultures

    let label language key =
        MessageCatalogs.render language key Map.empty

    let actorName language actor =
        ActorPresentation.displayName (MessageCatalogs.render language) actor

    let term language vocabulary value =
        label
            language
            (PresentationKeys.code (Vocabulary.name vocabulary) (Vocabulary.token vocabulary value))

    let principalKind language value =
        term language PrincipalKind.vocabulary value

    let line language key value = label language key + ": " + value

    let number (value: int64) =
        value.ToString(CultureInfo.InvariantCulture)

    let private exactMoney (language: string) canonical code scale =
        let parts = (canonical: string).Split '.'
        let culture = displayCulture language

        let whole =
            BigInteger.Parse(parts[0], CultureInfo.InvariantCulture).ToString("N0", culture)

        let fraction = if parts.Length = 1 then "" else parts[1]
        let padded = fraction.PadRight(scale, '0')

        whole
        + (if padded = "" then
               ""
           else
               culture.NumberFormat.NumberDecimalSeparator + padded)
        + " "
        + code

    let moneyWithScale language value scale =
        exactMoney language (Money.format value) (Money.currency value) scale

    let money language value =
        moneyWithScale language value (Money.displayScale value)

    let moneyTotalWithScale language currency total scale =
        exactMoney language (DecimalTotal.format total) (Currency.format currency) scale

    let moneyTotal language currency total =
        moneyTotalWithScale language currency total (DecimalTotal.displayScale currency total)

    // These are .NET format strings, owned by the terminal renderer rather than the browser policy.
    let calendarDate language (value: DateOnly) =
        let pattern =
            match language with
            | "ko" -> "yyyy년 M월 d일"
            | "zh-Hans"
            | "zh-Hant"
            | "ja" -> "yyyy年M月d日"
            | "es" -> "d 'de' MMM 'de' yyyy"
            | "de" -> "d. MMM yyyy"
            | _ -> "d MMM yyyy"

        value.ToString(pattern, displayCulture language)

    let date language value =
        CalendarDate.value value |> calendarDate language

    let country (language: string) code =
        try
            let name = RegionInfo(language + "-" + code).NativeName
            if name = code then code else name + " (" + code + ")"
        with :? ArgumentException ->
            code

    let boolean value = if value then "true" else "false"

    let instant (value: DateTimeOffset) =
        value.ToUniversalTime().ToString(JsonFormats.InstantFormat, CultureInfo.InvariantCulture)

    let displayed language =
        function
        | None -> "—"
        | Some text ->
            let warning = TextDisplay.warning text
            let text = TerminalText.data text

            if warning.CodePoints.IsEmpty then
                text
            else
                let points =
                    warning.CodePoints @ (if warning.Truncated then [ "…" ] else [])
                    |> String.concat ", "

                text
                + " ["
                + MessageCatalogs.renderCli language (CliMessage.CharacterWarning points)
                + "]"

    let narrative language (value: string option) =
        match value with
        | None -> "—"
        | Some text ->
            // LF is expected paragraph layout, not a character warning. The stored value is untouched.
            let warning = TextDisplay.warning (text.Replace('\n', ' '))
            let rendered = TerminalText.paragraphs text

            if warning.CodePoints.IsEmpty then
                rendered
            else
                let points =
                    warning.CodePoints @ (if warning.Truncated then [ "…" ] else [])
                    |> String.concat ", "

                rendered
                + " ["
                + MessageCatalogs.renderCli language (CliMessage.CharacterWarning points)
                + "]"

    let fieldValue language field value =
        match FieldPresentation.descriptor field with
        | ScalarRule.PlainText { AllowsLineFeed = true } -> narrative language value
        | _ -> displayed language value

    let nextLines language =
        function
        | None -> []
        | Some value -> [ line language (CliMessages.key CliMessage.NextLabel) value ]
