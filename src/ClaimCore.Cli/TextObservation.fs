namespace ClaimCore.Cli

open System
open System.Globalization
open ClaimCore.Application.Contracts
open ClaimCore.Protocol

/// Observation belongs to the installation snapshot, never the client's current clock.
module internal TextObservation =
    let line (language: string) (basis: WorkDateBasis) =
        let culture = TextReplyFormat.displayCulture language

        let observed =
            try
                TimeZoneInfo
                    .ConvertTime(
                        basis.ObservedAt,
                        TimeZoneInfo.FindSystemTimeZoneById basis.TimeZoneId
                    )
                    .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            with
            | :? TimeZoneNotFoundException
            | :? InvalidTimeZoneException ->
                basis.ObservedAt
                    .ToUniversalTime()
                    .ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)

        MessageCatalogs.renderCli
            language
            (CliMessage.Observed(
                observed,
                basis.BusinessDate.ToString("d MMM yyyy", culture),
                TerminalText.data basis.TimeZoneId
            ))


    let footer language count cursor basis allowAll =
        [
            yield MessageCatalogs.renderCli language (CliMessage.PageCount(string count))
            match cursor with
            | Some cursor ->
                yield
                    MessageCatalogs.renderCli
                        language
                        (if allowAll then
                             CliMessage.PageMoreAll(TerminalText.data cursor)
                         else
                             CliMessage.PageMore(TerminalText.data cursor))
            | None -> ()
            yield line language basis
        ]
