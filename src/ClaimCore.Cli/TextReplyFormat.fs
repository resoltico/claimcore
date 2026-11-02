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

    let term language vocabulary value =
        label language (Vocabulary.messageKey vocabulary value "label")

    let principalKind language value =
        term language PrincipalKind.vocabulary value

    let line language key value = label language key + ": " + value

    let number (value: int64) =
        value.ToString(CultureInfo.InvariantCulture)

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

    let nextLines language =
        function
        | None -> []
        | Some value -> [ line language (CliMessages.key CliMessage.NextLabel) value ]
