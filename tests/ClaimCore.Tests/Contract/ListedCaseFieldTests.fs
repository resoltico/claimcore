module ClaimCore.Tests.ListedCaseFieldTests

open System.Text
open Expecto
open ClaimCore.Domain
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
                let codec = ListedCaseFieldCodecs.create CaseFields.declarations

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
            testCase
                "an orphan listed currency cannot silently disappear from human columns"
                (fun () ->
                    let broken =
                        CaseFields.declarations
                        |> List.map (fun field ->
                            if field.Field = BusinessField.ClaimedAmount then
                                { field with Listed = false }
                            else
                                field)

                    Expect.throws
                        (fun () -> ListedCaseFieldCodecs.create broken |> ignore)
                        "A declaration must keep the complete pair")
            testCase
                "a test-only listed date reaches wire and generic human formatting without a new selector"
                (fun () ->
                    let fields =
                        {
                            CaseFields.CaseReference = Inputs.reference "LIST-1"
                            Status = CaseStatus.Opened
                            Registration = Inputs.admittedRegistration Inputs.registration
                            Progress = PaymentProgress.Undecided
                        }

                    let expanded =
                        CaseFields.declarations
                        |> List.map (fun field ->
                            if field.Field = BusinessField.IncidentDate then
                                { field with
                                    Listed = true
                                    Prominence = Prominence.Primary
                                }
                            else
                                field)

                    let values = CaseFields.listedValues expanded fields
                    let codec = ListedCaseFieldCodecs.create expanded
                    let wire = codec.Write values
                    let expectedDate = Registration.incidentDate fields.Registration

                    Expect.equal
                        (JsonValue.tryMember "incidentDate" wire)
                        (Some(JsonValue.String(CalendarDate.format expectedDate)))
                        "The wire row includes the declaration's additional date"

                    Expect.equal
                        (codec.Read "$" wire |> required)
                        values
                        "The expanded type retains validated values"

                    let describe declarations =
                        Descriptor.caseFields declarations (fun () -> Some values)

                    Expect.isFalse
                        (describe CaseFields.declarations
                         |> List.exists (fun entry -> entry.Key = "incidentDate"))
                        "The unmodified declaration is a negative control"

                    let date =
                        describe expanded |> List.find (fun entry -> entry.Key = "incidentDate")

                    Expect.equal
                        (date.Read())
                        (PresentedValue.Date expectedDate)
                        "The core descriptor preserves the semantic date kind"

                    Expect.equal
                        (TextDescriptorValue.render "en" (date.Read()))
                        (TextReplyFormat.date "en" expectedDate)
                        "The real generic CLI formatter needs no field-specific branch")
        ]
