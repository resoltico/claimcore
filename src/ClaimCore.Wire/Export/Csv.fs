namespace ClaimCore.Wire.Export

open System
open System.Globalization
open System.Text
open ClaimCore.Json
open ClaimCore.Domain
open ClaimCore.Presentation

/// DATA export owns RFC 4180 records and canonical wire scalars.
module Csv =
    let row values =
        String.concat "," (values |> List.map CsvCell.format) + "\r\n"
        |> Encoding.UTF8.GetBytes

    let rows values = values |> List.map row |> Array.concat

    let instant (value: DateTimeOffset) =
        value.ToUniversalTime().ToString(JsonFormats.InstantFormat, CultureInfo.InvariantCulture)

    let actorName render actor =
        ActorPresentation.displayName render actor

    let boolean value = if value then "true" else "false"
