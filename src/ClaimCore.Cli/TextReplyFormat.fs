namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json

module internal TextReplyFormat =

    let label language key =
        MessageCatalogs.render language key Map.empty

    let actorName language actor =
        ActorPresentation.displayName (MessageCatalogs.render language) actor

    let term language vocabulary value =
        label language (Vocabulary.messageKey vocabulary value "label")

    let principalKind language value =
        term language PrincipalKind.vocabulary value

    let line language key value = label language key + ": " + value

    let number (value: int64) =
        value.ToString(CultureInfo.InvariantCulture)

    let money (language: string) value =
        let precision = Money.displayScale value
        let format = "N" + precision.ToString(CultureInfo.InvariantCulture)

        (Money.value value).ToString(format, CultureInfo.GetCultureInfo language)
        + " "
        + Money.currency value

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
