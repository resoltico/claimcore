namespace ClaimCore.ContractGeneration

open System
open System.Globalization
open System.Text.Json

[<NoEquality; NoComparison>]
type internal ScalarCorpusCase =
    {
        Identifier: string
        Endpoint: string
        Value: JsonElement
    }

[<RequireQualifiedAccess>]
module internal ScalarCorpus =
    let private cases endpoint values =
        values
        |> List.map (fun (suffix, value) ->
            {
                Identifier = "scalar-" + suffix + "-" + endpoint
                Endpoint = endpoint
                Value = value
            })

    let private text identifier source expected replacements =
        CorpusJson.textVariants identifier source expected replacements

    let private caseValues source =
        let maximum = Int64.MaxValue.ToString(CultureInfo.InvariantCulture)
        let overflow = (uint64 Int64.MaxValue + 1UL).ToString(CultureInfo.InvariantCulture)

        text
            "date"
            source
            CliCorpusValues.fields.IncidentDate
            [ "year-zero", "0000-08-01"; "invalid-calendar", "2026-02-30" ]
        @ text "text" source CliCorpusValues.fields.CaseReference [ "empty", "" ]
        @ CorpusJson.domainTextBoundaries source CliCorpusValues.fields.CaseReference 80
        @ text
            "revision"
            source
            "1"
            [
                "leading-zero", "01"
                "maximum", maximum
                "overflow", overflow
                "line-break", "1\n"
            ]
        @ text
            "amount"
            source
            CliCorpusValues.fields.ClaimedAmount
            [
                "negative", "-1"
                "exponent", "1e2"
                "excess-fraction", "1.00000"
                "line-break", "1\n"
            ]
        @ text
            "currency"
            source
            CliCorpusValues.fields.ClaimedCurrency
            [ "lowercase", "eur"; "non-ascii", "EÜR" ]
        |> cases "case.get"

    let private prepareValues source uuidSource (operationId: Guid) format =
        text
            "uuid"
            uuidSource
            (operationId.ToString("D"))
            [
                "uppercase", operationId.ToString("D").ToUpperInvariant()
                "zero", Guid.Empty.ToString("D")
            ]
        @ text
            "timestamp"
            source
            (CliCorpusValues.timestamp.ToUniversalTime().ToString("O"))
            [ "shape", "2026-09-09T10:11:12Z" ]
        @ text
            "digest"
            source
            CliCorpusValues.digest
            [
                "uppercase", CliCorpusValues.digest.ToUpperInvariant()
                "short", String.replicate 63 "a"
                "long", String.replicate 65 "a"
            ]
        @ CorpusJson.integerPropertyVariants
            "canonical-format"
            source
            "canonicalCommandFormat"
            format
            [ "lower", format - 1; "future", format + 1 ]
        |> cases "command.prepare"

    let all caseSource prepareSource uuidSource operationId format =
        caseValues caseSource
        @ prepareValues prepareSource uuidSource operationId format
