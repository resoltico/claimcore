namespace ClaimCore.Cli

open System
open System.Globalization
open ClaimCore.Application.Contracts
open ClaimCore.Protocol

/// Observation belongs to the installation snapshot, never the client's current clock.
module internal TextObservation =
    let line (language: string) (basis: WorkDateBasis) =
        let culture = CultureInfo.GetCultureInfo language

        let observed =
            TimeZoneInfo.ConvertTime(
                basis.ObservedAt,
                TimeZoneInfo.FindSystemTimeZoneById basis.TimeZoneId
            )

        MessageCatalogs.renderCli
            language
            (CliMessage.Observed(
                observed.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                basis.BusinessDate.ToString("d MMM yyyy", culture),
                TerminalText.data basis.TimeZoneId
            ))
