module ClaimCore.Tests.PaymentActionTests

open System
open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json
open ClaimCore.TestSupport

let private parent =
    CaseFixtures.caseId (Guid.Parse "10000000-0000-7000-8000-000000000001")

let private payment =
    CaseFixtures.operationId (Guid.Parse "10000000-0000-7000-8000-000000000002")

let private revision = CaseFixtures.revision 3L
let private position = EffectPosition.create Sequence.first 0 |> Option.get

let private entry =
    PaymentEntry.create
        payment
        PaymentDirection.Paid
        (DomainInputs.money "25" "EUR")
        (DomainInputs.date "2026-08-20")
        None
        None
    |> CaseBuilders.accepted

let private item actions =
    {
        CasePaymentItem.Entry = entry
        Position = position
        Actions = actions |> List.map TaskId.ofLiteral
    }

let private cases =
    [
        "correction only", [ "case.correctPayment" ]
        "removal only", [ "case.removePayment" ]
        "both in supplied order", [ "case.correctPayment"; "case.removePayment" ]
        "neither", []
    ]

[<Tests>]
let tests =
    testList
        "payment item action contracts"
        [
            for name, tasks in cases do
                testCase
                    ("paged presentation attaches " + name + " on the same selected binding")
                    (fun () ->
                        let page =
                            {
                                CasePaymentPage.Basis = DomainInputs.workBasis
                                CaseId = parent
                                Reference = DomainInputs.reference "CC-H652-1AAT-SZCY-BSHN-04"
                                Revision = revision
                                WorkRevision = Revision.zero
                                State =
                                    CaseState.Active(
                                        CaseFixtures.finance
                                            (Some(CaseFixtures.decision DomainInputs.decision))
                                            [ entry ]
                                    )
                                Items = [ item tasks ]
                                Payees = []
                                ClosureOutcome = None
                                NextCursor = None
                            }

                        let rows =
                            PaymentsScreen.page page
                            |> _.Regions
                            |> List.collect (fun region ->
                                match region.Content with
                                | RegionContent.Table table -> table.Rows
                                | _ -> [])

                        let controls = (List.exactlyOne rows).Actions

                        Expect.equal
                            (controls |> List.map (_.Task >> TaskId.token))
                            tasks
                            "Presentation neither infers another capability nor reorders supplied controls"

                        for control in controls do
                            Expect.equal
                                control.Binding
                                (Some(
                                    Binding.CasePayment(CaseSelector.Id parent, revision, payment)
                                ))
                                "Every action stays attached to the same exact row and reviewed case revision"

                            if control.Task = TaskId.ofLiteral "case.correctPayment" then
                                Expect.equal
                                    (Map.tryFind
                                        (TaskInputId.Target(
                                            InputTarget.Field BusinessField.PaidCurrency
                                        ))
                                        control.Seeds)
                                    (Some(InputValue.Text "EUR"))
                                    "Correction retains the supplied currency seed"
                            else
                                Expect.isEmpty
                                    control.Seeds
                                    "Removal acquires no correction inputs"

                        let encoded = PaymentPageCodecs.item.Write(item tasks) |> WireWriter.bytes

                        Expect.equal
                            (Codec.decode
                                Limits.MaximumResponseBytes
                                PaymentPageCodecs.item
                                encoded)
                            (Ok(item tasks))
                            "Wire preserves the exact supplied subset and order")
            testCase "published payment actions are the finite owned composition IDs" (fun () ->
                use document =
                    JsonDocument.Parse(WireWriter.bytes OpenApi.document |> ReadOnlyMemory<byte>)

                let properties =
                    document.RootElement
                        .GetProperty("components")
                        .GetProperty("schemas")
                        .GetProperty("CasePaymentItem")
                        .GetProperty("properties")

                Expect.equal
                    (properties.EnumerateObject() |> Seq.map _.Name |> Set.ofSeq)
                    (set [ "entry"; "position"; "actions" ])
                    "The retired removal flag has no compatibility path"

                let choices =
                    properties
                        .GetProperty("actions")
                        .GetProperty("items")
                        .GetProperty("enum")
                        .EnumerateArray()
                    |> Seq.map (fun value -> value.GetString() |> Option.ofObj |> Option.get)
                    |> Seq.toList

                Expect.equal
                    choices
                    [ "case.removePayment"; "case.correctPayment" ]
                    "The contract does not admit arbitrary ledger task IDs")
            testCase "retired flags foreign task IDs and non-list action claims refuse" (fun () ->
                let envelope action =
                    """{"entry":{"paymentId":"10000000-0000-7000-8000-000000000002","paymentDate":"2026-08-20","paidAmount":"25"},"position":{"sequence":1,"effectIndex":0},REPLACE}"""
                        .Replace("REPLACE", action)

                for actions in
                    [
                        "\"canRemove\":true"
                        "\"actions\":true"
                        "\"actions\":[\"note.remove\"]"
                        "\"actions\":[],\"canRemove\":false"
                    ] do
                    Expect.isError
                        (Codec.decode
                            Limits.MaximumResponseBytes
                            PaymentPageCodecs.item
                            (Encoding.UTF8.GetBytes(envelope actions)))
                        "The closed replacement grammar refuses retired or inferred claims")
        ]
