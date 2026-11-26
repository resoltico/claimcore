module ClaimCore.Tests.ScalarRuleTests

open ClaimCore.TestSupport



open ClaimCore.Wire
open ClaimCore.Presentation

open System
open Expecto
open ClaimCore.Domain

[<NoEquality; NoComparison>]
type private GrammarScenario =
    {
        Field: BusinessField
        Parse: string -> Result<string, Violation>
        WrongParser: string -> Result<string, Violation>
        Invalid: string
        Valid: string
        Expected: Violation
    }

let private scalar name = FieldPresentation.descriptor name

[<Tests>]
let listPresentation =
    testCase "Presentation owns fact order listing and monetary prominence" (fun () ->
        Expect.equal
            (FieldPresentation.declarations |> List.map _.Field |> Set.ofList)
            (BusinessField.declarations |> List.map _.Field |> Set.ofList)
            "Every Domain fact has an explicit human order"

        Expect.equal
            (FieldPresentation.declarations
             |> List.filter (fun field -> FieldPresentation.listed field.Field)
             |> List.map _.Field
             |> Set.ofList)
            (Set.ofList
                [
                    BusinessField.ClaimedAmount
                    BusinessField.ClaimedCurrency
                    BusinessField.PayableAmount
                    BusinessField.PayableCurrency
                ])
            "Financial facts include both complete money pairs"

        for amount, currency in
            [
                BusinessField.ClaimedAmount, BusinessField.ClaimedCurrency
                BusinessField.PayableAmount, BusinessField.PayableCurrency
            ] do
            let amount = FieldPresentation.prominence amount
            let currency = FieldPresentation.prominence currency

            Expect.equal amount Prominence.Primary "Money remains visible before optional context"

            Expect.equal currency amount "Width reduction cannot orphan the currency"

        Expect.equal
            (FieldPresentation.prominence BusinessField.InsurerClaimReference)
            Prominence.Detail
            "Insurer is supplementary list context"

        Expect.equal
            (FieldPresentation.prominence BusinessField.IncidentDescription)
            Prominence.Detail
            "Narrative remains available in details")

let private expectText name maximum =
    match scalar name with
    | ScalarRule.Text constraints ->
        Expect.equal constraints.Format.MinimumCharacters 1 "Permanent nonempty text"
        Expect.isNone constraints.Format.Grammar "Business text has no admission grammar"

        Expect.equal constraints.MaximumCharacters (Some maximum) "Text character bound"
    | _ -> failtest (BusinessField.key name + " must be a text scalar.")

let private textAndDateRuleTests =
    testList
        "text and date scalar catalog"
        [
            testCase "text rules retain permanent syntax and input bounds" (fun () ->
                expectText BusinessField.PolicyNumber 80
                expectText BusinessField.InsurerClaimReference 80

                match scalar BusinessField.IncidentCountry with
                | ScalarRule.Text constraints ->
                    Expect.equal constraints.Format.Grammar (Some "[A-Z]{2}") "Permanent syntax"
                    Expect.isNone constraints.MaximumCharacters "No separate admission bound"
                | _ -> failtest "Expected a country scalar"

                match scalar BusinessField.CaseReference with
                | ScalarRule.Text constraints ->
                    Expect.equal
                        constraints.MaximumCharacters
                        (Some 32)
                        "Exact reference length bound"

                    Expect.isSome constraints.Format.Grammar "Public lexical grammar is present"
                | _ -> failtest "Expected a reference scalar")
            testCase "date rules retain the Gregorian input range" (fun () ->
                for name in
                    [
                        BusinessField.IncidentDate
                        BusinessField.ReceivedDate
                        BusinessField.PaymentDecisionDate
                        BusinessField.PaymentDate
                    ] do
                    match scalar name with
                    | ScalarRule.CalendarDate rule ->
                        Expect.equal rule.ExactFormat "yyyy-MM-dd" "Exact format"
                        Expect.equal rule.Minimum DateOnly.MinValue "Lower bound"
                        Expect.equal rule.Maximum DateOnly.MaxValue "Upper bound"
                    | _ -> failtest (BusinessField.key name + " must be a calendar-date scalar."))
        ]

let private numericAndStatusRuleTests =
    testList
        "numeric and status scalar catalog"
        [
            testCase "amount and currency retain exact syntax and bounds" (fun () ->
                for name in [ BusinessField.ClaimedAmount; BusinessField.PayableAmount ] do
                    match scalar name with
                    | ScalarRule.Amount rule ->
                        Expect.equal
                            rule.Text.Format.Grammar
                            (Some rule.Grammar)
                            "Amount syntax is permanent"

                        Expect.isNone rule.Text.MaximumCharacters "No admission-only amount syntax"
                        Expect.equal rule.Grammar "(0|[1-9][0-9]{0,17})(\\.[0-9]{1,4})?" "Grammar"
                        Expect.equal rule.MaximumIntegerDigits 18 "Whole-digit bound"
                        Expect.equal rule.MaximumFractionalDigits 4 "Fractional-digit bound"
                    | _ -> failtest (BusinessField.key name + " must be an amount scalar.")

                for name in [ BusinessField.ClaimedCurrency; BusinessField.PayableCurrency ] do
                    match scalar name with
                    | ScalarRule.Currency rule ->
                        Expect.equal
                            rule.Text.Format.Grammar
                            (Some rule.Grammar)
                            "Currency syntax is permanent"

                        Expect.isNone
                            rule.Text.MaximumCharacters
                            "No admission-only currency syntax"

                        Expect.equal rule.Grammar "[A-Z]{3}" "Grammar"
                        Expect.equal rule.ExactCharacters 3 "Exact length"
                    | _ -> failtest (BusinessField.key name + " must be a currency scalar."))
            testCase "case status admits only opened and closed" (fun () ->
                match scalar BusinessField.Status with
                | ScalarRule.CaseStatus rule ->
                    Expect.equal
                        rule.AllowedValues
                        [ CaseStatus.Opened; CaseStatus.Closed ]
                        "No renderer-owned status"
                | _ -> failtest "status must be a case-status scalar.")
        ]

[<Tests>]
let permanentBoundaries =
    testList
        "typed service boundaries"
        [
            testCase "Domain revisions agree with the exact JSON integer bound" (fun () ->
                Expect.equal
                    Revision.Maximum
                    ClaimCore.Json.JsonValue.MaximumInteger
                    "Independent owners must agree on representable revisions")
            testCase "owner provisioning cannot enter ordinary authored admission" (fun () ->
                let request: ClaimCore.Application.Contracts.SubmissionEnvelope<int64, Guid> =
                    {
                        OperationId = Guid.Parse "90000000-0000-7000-8000-000000000001"
                        Task = TaskId.ofLiteral "people.provisionOwner"
                        Binding = ClaimCore.Application.Contracts.OperationBinding.People(None, 0L)
                        Inputs = Map.empty
                    }

                Expect.isError
                    (ClaimCore.Application.Contracts.OperationSubmission.admit request)
                    "Provisioning is admitted exclusively through its dedicated boundary")

        ]

[<Tests>]
let tests =
    testList "scalar admission contracts" [ textAndDateRuleTests; numericAndStatusRuleTests ]

[<Tests>]
let grammarDiagnostics =
    testCase "value modules own grammar diagnostics, with wrong-parser negative controls" (fun () ->
        let reference value =
            CaseReference.parse value |> Result.map CaseReference.format

        let country value =
            CountryCode.parse value |> Result.map CountryCode.format

        for scenario in
            [
                {
                    Field = BusinessField.CaseReference
                    Parse = reference
                    WrongParser = country
                    Invalid = "has space"
                    Valid = "UNIT-1"
                    Expected = Violation.ReferenceFormat
                }
                {
                    Field = BusinessField.IncidentCountry
                    Parse = country
                    WrongParser = reference
                    Invalid = "Latvia"
                    Valid = "LV"
                    Expected = Violation.CountryFormat
                }

            ] do
            let field = scenario.Field
            let parse = scenario.Parse
            let wrongParser = scenario.WrongParser
            let invalid = scenario.Invalid
            let valid = scenario.Valid
            let expected = scenario.Expected
            let target = InputTarget.Field field

            let check parser =
                Expect.equal
                    (parser invalid)
                    (Error expected)
                    "The owning parser supplies its diagnostic"

            check parse

            let candidate value =
                let values =
                    Map.add
                        (TaskInputId.Target(InputTarget.Field field))
                        (InputValue.Text value)
                        ClaimCore.Tests.TaskBuilders.openingInputs.Values

                InputMap.create values Map.empty |> Option.get

            let issues = ClaimCore.Domain.Tasks.Case.Open.check (candidate invalid)

            Expect.equal
                (issues |> List.map (fun issue -> issue.Input, issue.Violation))
                [ TaskInputId.Target target, expected ]
                "The task keeps the target and value-module diagnostic"

            Expect.isEmpty
                (ClaimCore.Domain.Tasks.Case.Open.check (candidate valid))
                "Valid control is admitted"

            Expect.throws (fun () -> check wrongParser) "Using the other value parser is detected")

[<Tests>]
let supportedCodeVocabularies =
    testCase
        "Core country and currency eligibility uses the adopted supported vocabularies"
        (fun () ->
            for code in [ "LV"; "XK"; "US"; "DE"; "TW" ] do
                Expect.isOk (CountryCode.parse code) ("Supported country " + code)

            for code in [ "QQ"; "SU"; "DD"; "EU"; "XA"; "AA"; "UK"; "AC" ] do
                Expect.equal
                    (CountryCode.parse code)
                    (Error Violation.CountryFormat)
                    ("Unassigned/reserved country " + code)

            for code in [ "EUR"; "XAF"; "XCD"; "XOF"; "XPF"; "XCG" ] do
                Expect.isOk (Currency.parse code) ("Supported national/regional currency " + code)

            for code in
                [
                    "QQQ"
                    "XXX"
                    "XTS"
                    "XAU"
                    "XDR"
                    "XAD"
                    "BOV"
                    "CLF"
                    "COU"
                    "CHE"
                    "CHW"
                    "MXV"
                    "USN"
                    "UYI"
                    "UYW"
                ] do
                Expect.equal
                    (Currency.parse code)
                    (Error Violation.CurrencyFormat)
                    ("Unsupported or fund currency " + code))
