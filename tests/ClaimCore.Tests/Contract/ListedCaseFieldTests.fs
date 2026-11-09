module ClaimCore.Tests.ListedCaseFieldTests

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.TestSupport

let private required =
    function
    | Ok value -> value
    | Error error -> failtestf "%A" error

[<Tests>]
let tests =
    testList
        "declaration-generated listed fields"
        [
            testCase "payable presence is a complete decision group in the wire schema" (fun () ->
                let codec = ListedCaseFieldCodecs.create CaseSummaryFields.declarations

                for amount, currency, expected in
                    [
                        "null", "null", true
                        "\"750\"", "\"EUR\"", true
                        "null", "\"EUR\"", false
                        "\"750\"", "null", false
                    ] do
                    let raw =
                        sprintf
                            """{"claimantName":"Ann","insurerName":"Ins","claimedAmount":"1000","claimedCurrency":"EUR","payableAmount":%s,"payableCurrency":%s}"""
                            amount
                            currency

                    Expect.equal
                        (Codec.decode 4096 codec (Encoding.UTF8.GetBytes raw) |> Result.isOk)
                        expected
                        "Clients cannot receive a half-null money pair"

                match Map.find "ListedCaseFields" codec.Definitions with
                | Schema.OneOf(variants, None) ->
                    Expect.equal
                        variants.Length
                        2
                        "The generated client receives complete-or-absent alternatives"
                | other -> failtestf "Expected presence alternatives, got %A" other)
            testCase "query money selection cannot orphan a currency" (fun () ->
                for removed in
                    [
                        BusinessField.ClaimedAmount
                        BusinessField.ClaimedCurrency
                        BusinessField.PayableAmount
                        BusinessField.PayableCurrency
                    ] do
                    let broken =
                        CaseSummaryFields.declarations
                        |> List.filter (fun field -> field.Field <> removed)

                    Expect.throws
                        (fun () -> ListedCaseFieldCodecs.create broken |> ignore)
                        "The query transport retains both components in either direction")
            testCase "query fact selection is independent of human table selection" (fun () ->
                let fields =
                    {
                        CaseFields.CaseReference = Inputs.reference "LIST-1"
                        Status = CaseStatus.Opened
                        Registration = Inputs.admittedRegistration Inputs.registration
                        Progress = PaymentProgress.Undecided
                    }

                let expanded =
                    CaseSummaryFields.declarations
                    @ [ BusinessField.describe BusinessField.IncidentDate ]

                let values = CaseSummaryFields.values expanded fields
                let codec = ListedCaseFieldCodecs.create expanded
                let wire = codec.Write values
                let expectedDate = Registration.incidentDate fields.Registration

                Expect.equal
                    (JsonValue.tryMember "incidentDate" wire)
                    (Some(JsonValue.String(CalendarDate.format expectedDate)))
                    "The query explicitly selects the additional date"

                Expect.equal (codec.Read "$" wire |> required) values "Validated values survive"

                let listed = ScreenEntries.caseFields expanded (fun () -> Some values)

                Expect.isFalse
                    (listed |> List.exists (fun entry -> entry.Key = "incidentDate"))
                    "Query selection does not change human table layout"

                let date =
                    ScreenEntries.fields expanded (fun () -> Some values)
                    |> List.find (fun entry -> entry.Key = "incidentDate")

                Expect.equal (date.Read()) (PresentedValue.Date expectedDate) "Semantic date kind"

                Expect.equal
                    (TextDescriptorValue.render "en" (date.Read()))
                    (ReplyTextFormat.date "en" expectedDate)
                    "The generic CLI formatter consumes the supplied value")
        ]
