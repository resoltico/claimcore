module ClaimCore.Tests.PropertyHarness

open System
open System.Buffers.Binary
open System.Globalization
open System.Security.Cryptography
open System.Text
open Expecto
open Hedgehog
open Hedgehog.FSharp

type Profile =
    | Required
    | Extended

type Plan =
    | Standard of Profile * uint64
    | Recheck of Profile * propertyId: string * token: string

let private propertyIds =
    set
        [
            "CC-PROP-DATE-001"
            "CC-PROP-AMOUNT-001"
            "CC-PROP-CURRENCY-001"
            "CC-PROP-TEXT-001"
            "CC-PROP-RECORD-001"
            "CC-PROP-FINGERPRINT-001"
            "CC-PROP-TRANSITION-001"
            "CC-PROP-AVAILABILITY-001"
        ]

let private fixedBaseSeed = 0x434C41494D434F52UL

let private present name (values: Map<string, string>) =
    values
    |> Map.tryFind name
    |> Option.bind (fun value -> if String.IsNullOrEmpty(value) then None else Some value)

let private canonicalUInt64 (value: string) =
    match UInt64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
    | true, parsed when parsed.ToString(CultureInfo.InvariantCulture) = value -> Ok parsed
    | _ -> Error "The property base seed must be one canonical unsigned 64-bit integer."

let private rawRecheckToken (value: string) =
    match value.Split(':', 2, StringSplitOptions.None) with
    | [| "required"; token |] -> Ok(Required, token)
    | [| "extended"; token |] -> Ok(Extended, token)
    | _ -> Error "The property recheck token must retain its required or extended profile."

let private validateRecheckToken token =
    try
        let decoded = RecheckData.deserialize token

        if RecheckData.serialize decoded = token then
            Ok token
        else
            Error "The Hedgehog recheck token is not canonical."
    with :? ArgumentException ->
        Error "The Hedgehog recheck token is malformed."

let parsePlan (values: Map<string, string>) =
    let profile =
        present "CLAIMCORE_PROPERTY_PROFILE" values |> Option.defaultValue "required"

    let seed = present "CLAIMCORE_PROPERTY_BASE_SEED" values
    let recheckId = present "CLAIMCORE_PROPERTY_RECHECK_ID" values
    let recheckToken = present "CLAIMCORE_PROPERTY_RECHECK_TOKEN" values

    match profile, seed, recheckId, recheckToken with
    | "required", None, None, None -> Ok(Standard(Required, fixedBaseSeed))
    | "extended", Some rawSeed, None, None ->
        canonicalUInt64 rawSeed |> Result.map (fun value -> Standard(Extended, value))
    | "recheck", None, Some propertyId, Some token when Set.contains propertyId propertyIds ->
        rawRecheckToken token
        |> Result.bind (fun (origin, raw) ->
            validateRecheckToken raw
            |> Result.map (fun valid -> Recheck(origin, propertyId, valid)))
    | "recheck", None, Some _, Some _ -> Error "The property recheck ID is not registered."
    | _ -> Error "Use exactly one required, extended, or paired recheck property configuration."

let private environment () =
    [
        "CLAIMCORE_PROPERTY_PROFILE"
        "CLAIMCORE_PROPERTY_BASE_SEED"
        "CLAIMCORE_PROPERTY_RECHECK_ID"
        "CLAIMCORE_PROPERTY_RECHECK_TOKEN"
    ]
    |> List.choose (fun name ->
        Environment.GetEnvironmentVariable(name)
        |> Option.ofObj
        |> Option.map (fun value -> name, value))
    |> Map.ofList

let private plan =
    lazy
        (environment ()
         |> parsePlan
         |> Result.defaultWith (fun message -> invalidOp message))

let profile () =
    match plan.Value with
    | Standard(profile, _)
    | Recheck(profile, _, _) -> profile

let sequenceMaximum () =
    match profile () with
    | Required -> 20
    | Extended -> 100

let private testsFor =
    function
    | Required -> 200
    | Extended -> 5_000

let private seedFor (baseSeed: uint64) (propertyId: string) =
    let bytes = Array.zeroCreate<byte> 8
    BinaryPrimitives.WriteUInt64BigEndian(bytes, baseSeed)

    let hash =
        Array.append bytes (Encoding.UTF8.GetBytes(propertyId)) |> SHA256.HashData

    BinaryPrimitives.ReadUInt64BigEndian(ReadOnlySpan<byte>(hash)) |> Seed.from

let private config profile baseSeed propertyId =
    PropertyConfig.defaults
    |> PropertyConfig.withTests (LanguagePrimitives.Int32WithMeasure<tests>(testsFor profile))
    |> PropertyConfig.withSeed (seedFor baseSeed propertyId)

let private recheckText profile token =
    let prefix =
        match profile with
        | Required -> "required"
        | Extended -> "extended"

    prefix + ":" + token

let failureText propertyId profile (report: Report) =
    match report.Status with
    | Failed failure ->
        let token =
            failure.RecheckInfo
            |> Option.map (fun info -> RecheckData.serialize info.Data |> recheckText profile)
            |> Option.defaultValue "unavailable"

        $"Property {propertyId} failed after {int report.Tests} cases; recheck={token}."
    | GaveUp -> $"Property {propertyId} exhausted generation after {int report.Discards} discards."
    | OK -> $"Property {propertyId} passed."

let private requireStandard propertyId profile expected report =
    match report.Status with
    | OK when int report.Tests = expected && int report.Discards = 0 -> ()
    | OK -> failtest $"Property {propertyId} completed with an unexpected case/discard count."
    | Failed _
    | GaveUp -> failtest (failureText propertyId profile report)

let run propertyId (property: Property<bool>) =
    if not (Set.contains propertyId propertyIds) then
        invalidArg (nameof propertyId) "Use one registered property ID."

    let executable = Property.falseToFailure property

    match plan.Value with
    | Standard(profile, baseSeed) ->
        let report = Property.reportWith (config profile baseSeed propertyId) executable
        requireStandard propertyId profile (testsFor profile) report
    | Recheck(_, target, _) when target <> propertyId -> ()
    | Recheck(profile, _, token) ->
        let report = Property.reportRecheck token executable

        match report.Status with
        | OK -> ()
        | Failed _
        | GaveUp -> failtest (failureText propertyId profile report)
