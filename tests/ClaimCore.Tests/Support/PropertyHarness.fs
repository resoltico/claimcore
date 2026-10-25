module ClaimCore.Tests.PropertyHarness

open System
open System.Buffers.Binary
open System.Security.Cryptography
open System.Text
open Expecto
open Hedgehog
open Hedgehog.FSharp

let wireId endpoint response =
    let direction = if response then "REPLY" else "REQUEST"

    "CC-PROP-WIRE-"
    + direction
    + "-"
    + (ClaimCore.Contract.Api.id endpoint).Replace('.', '-').ToUpperInvariant()
    + "-001"

let private wirePropertyIds =
    [
        for endpoint in ClaimCore.Contract.Api.all do
            yield wireId endpoint false
            yield wireId endpoint true
    ]
    |> Set.ofList

let private propertyIds =
    set
        [
            "CC-PROP-CANONICAL-ROUNDTRIP-001"
            "CC-PROP-CANONICAL-SPELLING-001"
            "CC-PROP-STEWARDSHIP-001"
            "CC-PROP-STEWARDSHIP-RECORD-001"
            "CC-PROP-AUTHORITY-RECORD-001"
            "CC-PROP-LEDGER-PAYLOAD-001"
            "CC-PROP-LEDGER-HASH-001"
            "CC-PROP-DATE-001"
            "CC-PROP-AMOUNT-001"
            "CC-PROP-CURRENCY-001"
            "CC-PROP-TEXT-001"
            "CC-PROP-RECORD-001"
            "CC-PROP-FINGERPRINT-001"
            "CC-PROP-TRANSITION-001"
            "CC-PROP-AVAILABILITY-001"
            "CC-FUZZ-INVOCATION-001"
            "CC-FUZZ-RECORD-001"
        ]

let private fixedBaseSeed = 0x434C41494D434F52UL

let sequenceMaximum () = 20

let private caseCount = 200

// A failure remains a failure when minimization stops; retain its deterministic replay token.
let private shrinkMaximum = 100

let private seedFor (baseSeed: uint64) (propertyId: string) =
    let bytes = Array.zeroCreate<byte> 8
    BinaryPrimitives.WriteUInt64BigEndian(bytes, baseSeed)

    let hash =
        Array.append bytes (Encoding.UTF8.GetBytes(propertyId)) |> SHA256.HashData

    BinaryPrimitives.ReadUInt64BigEndian(ReadOnlySpan<byte>(hash)) |> Seed.from

let private config propertyId =
    PropertyConfig.defaults
    |> PropertyConfig.withTests (LanguagePrimitives.Int32WithMeasure<tests>(caseCount))
    |> PropertyConfig.withSeed (seedFor fixedBaseSeed propertyId)
    |> PropertyConfig.withShrinks (LanguagePrimitives.Int32WithMeasure<shrinks>(shrinkMaximum))

let failureText propertyId (report: Report) =
    match report.Status with
    | Failed failure ->
        let token =
            failure.RecheckInfo
            |> Option.map (fun info -> RecheckData.serialize info.Data)
            |> Option.defaultValue "unavailable"

        $"Property {propertyId} failed after {int report.Tests} cases; recheck={token}."
    | GaveUp -> $"Property {propertyId} exhausted generation after {int report.Discards} discards."
    | OK -> $"Property {propertyId} passed."

let private requireComplete propertyId report =
    match report.Status with
    | OK when int report.Tests = caseCount && int report.Discards = 0 -> ()
    | OK -> failtest $"Property {propertyId} completed with an unexpected case/discard count."
    | Failed _
    | GaveUp -> failtest (failureText propertyId report)

let run propertyId (property: Property<bool>) =
    if not (Set.contains propertyId (Set.union propertyIds wirePropertyIds)) then
        invalidArg (nameof propertyId) "Use one registered property ID."

    let executable = Property.falseToFailure property

    let report = Property.reportWith (config propertyId) executable
    requireComplete propertyId report
