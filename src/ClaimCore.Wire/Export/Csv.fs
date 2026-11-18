namespace ClaimCore.Wire.Export

open System
open System.Globalization
open System.Text
open ClaimCore.Json
open ClaimCore.Domain
open ClaimCore.Presentation

[<RequireQualifiedAccess>]
type CsvValue =
    | Text of string
    | ExactTotal of DecimalTotal

/// DATA export owns RFC 4180 records and canonical wire scalars.
module Csv =
    let private line formatted =
        String.concat "," formatted + "\r\n" |> Encoding.UTF8.GetBytes

    let row values =
        values |> List.map CsvCell.format |> line

    let typedRow values =
        values
        |> List.map (function
            | CsvValue.Text text -> CsvCell.format text
            | CsvValue.ExactTotal amount -> DecimalTotal.format amount)
        |> line

    let rows values = values |> List.map row |> Array.concat

    let instant (value: DateTimeOffset) =
        value.ToUniversalTime().ToString(JsonFormats.InstantFormat, CultureInfo.InvariantCulture)

    let actorName render actor =
        ActorPresentation.displayName render actor

    let boolean value = if value then "true" else "false"
