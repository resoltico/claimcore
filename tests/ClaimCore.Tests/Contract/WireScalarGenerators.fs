module ClaimCore.Tests.WireScalarGenerators

open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

open System
open System.Collections
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open Microsoft.FSharp.Reflection
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.TestSupport
open ClaimCore.Tests.PropertyHarness
open ClaimCore.Tests.WireSeedCorpus


let internal sequence generators =
    List.foldBack
        (fun generator state ->
            gen {
                let! head = generator
                let! tail = state
                return head :: tail
            })
        generators
        (Gen.constant [])

let internal scalarText =
    let atom =
        Gen.item
            [
                "x"
                "Ž"
                "東京"
                "😀"
                "é"
                "\""
                "\\"
                "\u0001"
                "\b"
                "\t"
                "\n"
                "\f"
                "\r"
                "\u001f"
                "\u2028"
                "\u2029"
                " "
            ]

    Gen.list (Range.constant 1 16) atom |> Gen.map (String.concat "")

let internal arbitraryText = Gen.frequency [ 1, Gen.constant ""; 4, scalarText ]

let internal guid =
    Gen.guid
    |> Gen.map (fun value ->
        if value = Guid.Empty then
            Guid.Parse "10000000-0000-4000-8000-000000000001"
        else
            value)

let internal date =
    Gen.int32 (Range.constant DateOnly.MinValue.DayNumber DateOnly.MaxValue.DayNumber)
    |> Gen.map (fun value ->
        DateOnly.FromDayNumber(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))

let internal instant =
    Gen.int64 (Range.constant 0L (DateTimeOffset.MaxValue.Ticks / 10L))
    |> Gen.map (fun value -> DateTimeOffset(value * 10L, TimeSpan.Zero))

let internal amount =
    gen {
        let! whole = Gen.uint64 (Range.constant 0UL 999_999_999_999_999_999UL)
        let! fraction = Gen.uint32 (Range.constant 0u 9999u)

        return
            whole.ToString(CultureInfo.InvariantCulture)
            + "."
            + fraction.ToString("D4", CultureInfo.InvariantCulture)
    }

let internal uppercase length =
    Gen.array (Range.singleton length) (Gen.char 'A' 'Z') |> Gen.map String

let internal digest =
    Gen.array
        (Range.singleton 64)
        (Gen.item [ '0' .. '9' ]
         |> fun numbers -> Gen.choice [ numbers; Gen.char 'a' 'f' ])
    |> Gen.map String

let internal reference = Gen.string (Range.constant 1 80) (Gen.char '!' '~')

let internal dates =
    set
        [
            "IncidentDate"
            "IncidentNotificationDate"
            "PaymentDecisionDate"
            "PaymentDate"
            "BusinessDate"
        ]

let internal tokens =
    set
        [
            "Kind"
            "Act"
            "act"
            "Role"
            "Status"
            "Detail"
            "Field"
            "RecommendedAction"
            "History"
            "Capability"
            "View"
            "Registration"
            "Decision"
            "Payment"
        ]

let internal name (context: string) = context.Split('.') |> Array.last

let internal responseStrings =
    Map.ofList
        [
            "CaseReference", reference
            "caseReference", reference
            "Reference", reference
            "IncidentCountry", uppercase 2
            "ClaimedCurrency", uppercase 3
            "PayableCurrency", uppercase 3
            "ClaimedAmount", amount
            "PayableAmount", amount
            "EntryHash", digest
            "ContractIdentity", digest
            "Issuer", arbitraryText
            "Subject", arbitraryText
        ]

let internal responseString field =
    if Set.contains field dates then
        date
    else
        Map.tryFind field responseStrings |> Option.defaultValue scalarText

let private presentationIdentifier field (context: string) =
    match field with
    | "LabelId"
    | "messageId"
    | "reasonId" -> true
    | "Key" ->
        [ "PresentedChange."; "PresentedField." ]
        |> List.exists (fun owner -> context.Contains(owner, StringComparison.Ordinal))
    | _ -> false

let private generatedResponseString field (context: string) =
    if presentationIdentifier field context then
        Gen.string (Range.constant 1 256) (Gen.char 'a' 'z')
    elif context.EndsWith("ActorContext.Issuer", StringComparison.Ordinal) then
        guid
        |> Gen.map (fun value -> "https://issuer-" + value.ToString("N") + ".example/")
    elif context.EndsWith("AccountSubject.Subject", StringComparison.Ordinal) then
        scalarText
    else
        responseString field

let internal generatedString response (source: Seeds) context =
    let field = name context

    if
        Set.contains field tokens
        || context.Contains("InstallationOffer.AuthorityAct", StringComparison.Ordinal)
    then
        Gen.item (source.Tokens[context] |> Seq.distinct |> Seq.toArray)
    elif field = "Cursor" || field = "AfterCursor" then
        scalarText
    elif response then
        generatedResponseString field context
    else
        arbitraryText

let internal integer response context =
    let field = name context

    let low, high =
        if context.Contains("InstallationOffer.Open", StringComparison.Ordinal) then
            0L, 0L
        elif field = "DisplayScale" then
            0L, 4L
        elif field = "Limit" then
            1L, 50L
        elif
            Set.contains
                field
                (set
                    [
                        "MaximumCharacters"
                        "MaximumFractionalDigits"
                        "maximumCharacters"
                        "fractionDigits"
                    ])
        then
            0L, int64 Int32.MaxValue
        elif
            Set.contains
                field
                (set
                    [
                        "MaximumPageSize"
                        "MaximumIntegerDigits"
                        "maximumPageSize"
                        "maximumGrants"
                        "integerDigits"
                    ])
        then
            1L, int64 Int32.MaxValue
        elif
            not response
            || field = "CaseCount"
            || field = "ExpectedRevision"
            || field = "expectedRevision"
        then
            0L, JsonValue.MaximumInteger
        else
            1L, JsonValue.MaximumInteger

    Gen.frequency
        [
            1, Gen.constant low
            1, Gen.constant high
            3, Gen.int64 (Range.constant low high)
        ]
