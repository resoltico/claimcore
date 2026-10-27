module ClaimCore.Tests.InputAccumulationTests

open System
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Tests.PropertyHarness
open ClaimCore.TestSupport

let private fields =
    [
        BusinessField.IncidentDate
        BusinessField.IncidentNotificationDate
        BusinessField.IncidentCountry
        BusinessField.ClaimantName
        BusinessField.InsurerName
        BusinessField.ClaimedAmount
        BusinessField.ClaimedCurrency
    ]

let private request mask : UnvalidatedCaseRequest =
    let invalid index = mask &&& (1 <<< index) <> 0
    let value index valid = if invalid index then "" else valid
    let original = Inputs.registration

    {
        OperationId =
            if invalid 0 then
                Guid.Empty
            else
                Guid.Parse "70000000-0000-4000-8000-000000000001"
        CaseReference = value 1 "SYNTHETIC-ACCUMULATION"
        ExpectedRevision = if invalid 2 then -1L else 1L
        Act =
            CaseAct.AmendRegistration
                {
                    IncidentDate = value 3 original.IncidentDate
                    IncidentNotificationDate = value 4 original.IncidentNotificationDate
                    IncidentCountry = value 5 original.IncidentCountry
                    ClaimantName = value 6 original.ClaimantName
                    InsurerName = value 7 original.InsurerName
                    ClaimedAmount = value 8 original.ClaimedAmount
                    ClaimedCurrency = value 9 original.ClaimedCurrency
                }
    }

let private expected mask =
    [
        yield InputTarget.OperationId
        yield InputTarget.Field BusinessField.CaseReference
        yield InputTarget.ExpectedRevision
        yield! fields |> List.map InputTarget.Field
    ]
    |> List.indexed
    |> List.choose (fun (index, target) ->
        if mask &&& (1 <<< index) <> 0 then Some target else None)

let private targets =
    function
    | Ok _ -> []
    | Error(Refusal.InvalidInput issues) -> InputIssues.toList issues |> List.map _.Target
    | Error _ -> failtest "Permanent field parsing cannot produce a state refusal"

let private catalogRank target =
    let inputs, _ =
        ClaimCore.Protocol.OperationCatalog.derive (
            ClaimCore.Protocol.CatalogOperation.Act(
                ClaimCore.Protocol.ActKind.Case CaseActKind.AmendRegistration
            )
        )

    let token = InputTarget.token target

    inputs
    |> List.findIndex (fun input -> List.contains token input.Binding.DiagnosticTargets)

let private complete mask actual =
    Expect.equal actual (expected mask) "Exactly the invalid fields are reported in catalog order"

[<Tests>]
let tests =
    testList
        "accumulated input admission"
        [
            testCase "random invalid subsets report every field in catalog order" (fun () ->
                let property =
                    property {
                        let! mask = Gen.int32 (Range.constant 0 1023)
                        let actual = Case.parse (request mask) |> targets
                        let ranks = actual |> List.map catalogRank
                        return actual = expected mask && ranks = List.sort ranks
                    }

                run "input issue subsets" property)
            testCase "a short-circuit negative control fails completeness" (fun () ->
                let mask = 1023
                let actual = Case.parse (request mask) |> targets
                complete mask actual

                Expect.throws
                    (fun () -> complete mask (List.take 1 actual))
                    "Keeping only one issue loses required diagnostics")
            testCase "the issue list cannot be empty" (fun () ->
                Expect.isNone
                    (InputIssues.ofList [])
                    "No empty invalid-input result can be constructed")
        ]

[<Tests>]
let phase2 =
    testCase
        "phase two keeps every date/text issue in derived catalog order and within the wire bound"
        (fun () ->
            let before = ClaimCore.Tests.Fixtures.paid ()
            let original = Inputs.registration

            let registration =
                { original with
                    IncidentDate = "2026-10-01"
                    IncidentNotificationDate = "2026-10-02"
                    ClaimantName = String('A', 201)
                    InsurerName = String('B', 201)
                }

            let correction: AuthoredCaseAct =
                CaseAct.Correct
                    {
                        Registration = Amendment.Replace registration
                        Decision =
                            Change.Replace
                                {
                                    PaymentDecisionDate = "2026-10-01"
                                    PayableAmount = "750"
                                    PayableCurrency = "EUR"
                                }
                        Payment = Change.Replace { PaymentDate = "2026-09-30" }
                    }

            let request =
                ClaimCore.Tests.Fixtures.request (Case.snapshot before).Revision correction

            let result = CaseFixtures.decide (DateOnly(2026, 7, 1)) request (Some before)
            let actual = result |> targets

            let expected =
                [
                    BusinessField.IncidentDate
                    BusinessField.IncidentNotificationDate
                    BusinessField.ClaimantName
                    BusinessField.InsurerName
                    BusinessField.PaymentDecisionDate
                    BusinessField.PaymentDecisionDate
                    BusinessField.PaymentDate
                    BusinessField.PaymentDate
                ]
                |> List.map InputTarget.Field

            Expect.equal
                actual
                expected
                "Chronological/future issues remain distinct but stable by their authored input"

            Expect.isLessThanOrEqual
                actual.Length
                ClaimCore.Protocol.DiagnosticCodecs.MaximumInputIssues
                "The worst phase-two field checks fit the bounded issue envelope"

            let inputs, _ =
                ClaimCore.Protocol.OperationCatalog.derive (
                    ClaimCore.Protocol.CatalogOperation.Act(
                        ClaimCore.Protocol.ActKind.Case CaseActKind.Correct
                    )
                )

            let ranks =
                actual
                |> List.map (fun target ->
                    let token = InputTarget.token target

                    inputs
                    |> List.findIndex (fun input ->
                        List.contains token input.Binding.DiagnosticTargets))

            Expect.equal
                ranks
                (List.sort ranks)
                "Core declaration order agrees with codec-derived catalog order")
