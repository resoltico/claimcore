namespace ClaimCore.Cli

open System
open System.Globalization
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

/// Observation belongs to the installation snapshot, never the client's current clock.
module internal TextObservation =
    let line (rendering: RenderingContext) (basis: WorkDateBasis) =
        let observed =
            try
                ReplyTextFormat.instantIn rendering basis.TimeZoneId basis.ObservedAt
            with
            | :? TimeZoneNotFoundException
            | :? InvalidTimeZoneException ->
                ReplyTextFormat.instantIn rendering "Etc/UTC" basis.ObservedAt + " UTC"

        MessageCatalogs.renderCli
            rendering.Language
            (CliMessage.Observed(
                observed,
                ReplyTextFormat.calendarDate rendering basis.BusinessDate,
                TerminalText.data basis.TimeZoneId
            ))


    let footer (rendering: RenderingContext) count cursor basis allowAll =
        [
            yield
                MessageCatalogs.renderCli
                    rendering.Language
                    (CliMessage.PageCount(ReplyTextFormat.number rendering (int64 count)))
            match cursor with
            | Some cursor ->
                yield
                    MessageCatalogs.renderCli
                        rendering.Language
                        (if allowAll then
                             CliMessage.PageMoreAll(TerminalText.data cursor)
                         else
                             CliMessage.PageMore(TerminalText.data cursor))
            | None -> ()
            yield line rendering basis
        ]
