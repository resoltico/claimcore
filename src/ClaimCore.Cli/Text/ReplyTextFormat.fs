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

    // Display cultures are immutable and independent of message language.
    // Explicit regional cultures keep numeric conventions independent of neutral-culture OS defaults.
    let private displayCultures =
        LocalizationPolicy.displayOptions
        |> List.map (fun option ->
            let culture = CultureInfo.GetCultureInfo(option.value).Clone() :?> CultureInfo

            culture.DateTimeFormat.Calendar <- GregorianCalendar()
            option.value, CultureInfo.ReadOnly culture)
        |> Map.ofList

    let displayCulture (rendering: RenderingContext) =
        Map.find rendering.DisplayLocale displayCultures

    // .NET supplies grouping/separators but does not substitute NativeDigits when formatting.
    // Apply its finite culture digit data only to freshly formatted numeric/date text, never inputs.
    let private digits rendering (value: string) =
        let native = (displayCulture rendering).NumberFormat.NativeDigits
        let result = StringBuilder(value.Length)

        for character in value do
            if character >= '0' && character <= '9' then
                result.Append(native[int character - int '0']) |> ignore
            else
                result.Append(character) |> ignore

        result.ToString()

    let number (rendering: RenderingContext) (value: int64) =
        value.ToString("N0", displayCulture rendering) |> digits rendering

    let label (rendering: RenderingContext) key =
        MessageCatalogs.renderWithNumber (number rendering) rendering.Language key Map.empty

    let actorName (rendering: RenderingContext) actor =
        ActorPresentation.displayName (MessageCatalogs.render rendering.Language) actor

    let term (rendering: RenderingContext) vocabulary value =
        label
            rendering
            (PresentationKeys.code (Vocabulary.name vocabulary) (Vocabulary.token vocabulary value))

    let principalKind (rendering: RenderingContext) value =
        term rendering PrincipalKind.vocabulary value

    let line (rendering: RenderingContext) key value = label rendering key + ": " + value

    let private exactMoney (rendering: RenderingContext) canonical code scale =
        let parts = (canonical: string).Split '.'
        let culture = displayCulture rendering

        let whole =
            BigInteger.Parse(parts[0], CultureInfo.InvariantCulture).ToString("N0", culture)

        let fraction = if parts.Length = 1 then "" else parts[1]
        let padded = fraction.PadRight(scale, '0')

        (whole
         + (if padded = "" then
                ""
            else
                culture.NumberFormat.NumberDecimalSeparator + padded)
         |> digits rendering)
        + " "
        + code

    let moneyWithScale (rendering: RenderingContext) value scale =
        exactMoney rendering (Money.format value) (Money.currency value) scale

    let money (rendering: RenderingContext) value =
        moneyWithScale rendering value (Money.displayScale value)

    let moneyTotalWithScale (rendering: RenderingContext) currency total scale =
        exactMoney rendering (DecimalTotal.format total) (Currency.format currency) scale

    let moneyTotal (rendering: RenderingContext) currency total =
        moneyTotalWithScale rendering currency total (DecimalTotal.displayScale currency total)

    // These are .NET format strings, owned by the terminal renderer rather than the browser policy.
    let calendarDate (rendering: RenderingContext) (value: DateOnly) =
        let pattern =
            match rendering.DisplayLocale with
            | "ko-KR" -> "yyyy년 M월 d일"
            | "zh-CN"
            | "zh-TW"
            | "ja-JP" -> "yyyy年M月d日"
            | "es-ES" -> "d 'de' MMM 'de' yyyy"
            | "de-DE" -> "d. MMM yyyy"
            | "en-US" -> "MMM d, yyyy"
            | _ -> "d MMM yyyy"

        value.ToString(pattern, displayCulture rendering) |> digits rendering

    let date (rendering: RenderingContext) value =
        CalendarDate.value value |> calendarDate rendering

    let country (rendering: RenderingContext) code =
        try
            let name = RegionInfo(rendering.Language + "-" + code).NativeName
            if name = code then code else name + " (" + code + ")"
        with :? ArgumentException ->
            code

    let boolean value = if value then "true" else "false"

    let instant (value: DateTimeOffset) =
        value.ToUniversalTime().ToString(JsonFormats.InstantFormat, CultureInfo.InvariantCulture)

    let instantIn (rendering: RenderingContext) timeZone (value: DateTimeOffset) =
        let local =
            TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById timeZone)

        let time =
            local.ToString(
                " " + (displayCulture rendering).DateTimeFormat.ShortTimePattern + " zzz",
                displayCulture rendering
            )
            |> digits rendering

        calendarDate rendering (DateOnly.FromDateTime local.DateTime) + time

    let displayed (rendering: RenderingContext) =
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
                + MessageCatalogs.renderCli rendering.Language (CliMessage.CharacterWarning points)
                + "]"

    let narrative (rendering: RenderingContext) (value: string option) =
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
                + MessageCatalogs.renderCli rendering.Language (CliMessage.CharacterWarning points)
                + "]"

    let fieldValue (rendering: RenderingContext) field value =
        match FieldPresentation.descriptor field with
        | ScalarRule.PlainText { AllowsLineFeed = true } -> narrative rendering value
        | _ -> displayed rendering value

    let nextLines (rendering: RenderingContext) =
        function
        | None -> []
        | Some value -> [ line rendering (CliMessages.key CliMessage.NextLabel) value ]
