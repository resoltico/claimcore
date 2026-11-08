module ClaimCore.Tests.PropertyHarness

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open Expecto
open Hedgehog
open Hedgehog.FSharp

let wireName endpoint response =
    let direction = if response then "reply" else "request"
    ClaimCore.Wire.Api.id endpoint + " " + direction + " round trip"

let private fixedBaseSeed = 0x434C41494D434F52UL

let sequenceMaximum () = 20

let private caseCount = 200

// A failure remains a failure when minimization stops; retain its deterministic replay token.
let private shrinkMaximum = 100

let private seedFor (baseSeed: uint64) (propertyName: string) =
    let bytes = Array.zeroCreate<byte> 8
    BinaryPrimitives.WriteUInt64BigEndian(bytes, baseSeed)

    let hash =
        Array.append bytes (Encoding.UTF8.GetBytes(propertyName)) |> SHA256.HashData

    BinaryPrimitives.ReadUInt64BigEndian(ReadOnlySpan<byte>(hash)) |> Seed.from

let private config propertyName =
    PropertyConfig.defaults
    |> PropertyConfig.withTests (LanguagePrimitives.Int32WithMeasure<tests>(caseCount))
    |> PropertyConfig.withSeed (seedFor fixedBaseSeed propertyName)
    |> PropertyConfig.withShrinks (LanguagePrimitives.Int32WithMeasure<shrinks>(shrinkMaximum))

let failureText propertyName (report: Report) =
    match report.Status with
    | Failed failure ->
        let token =
            failure.RecheckInfo
            |> Option.map (fun info -> RecheckData.serialize info.Data)
            |> Option.defaultValue "unavailable"

        $"Property {propertyName} failed after {int report.Tests} cases; recheck={token}.\n"
        + Report.render report
    | GaveUp ->
        $"Property {propertyName} exhausted generation after {int report.Discards} discards."
    | OK -> $"Property {propertyName} passed."

let private requireComplete propertyName report =
    match report.Status with
    | OK when int report.Tests = caseCount && int report.Discards = 0 -> ()
    | OK -> failtest $"Property {propertyName} completed with an unexpected case/discard count."
    | Failed _
    | GaveUp -> failtest (failureText propertyName report)

let run propertyName (property: Property<bool>) =
    let executable = Property.falseToFailure property

    let report = Property.reportWith (config propertyName) executable
    requireComplete propertyName report
