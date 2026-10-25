module ClaimCore.Tests.ScalarRuleTests



open ClaimCore.Contract

open System
open Expecto
open ClaimCore.Domain

let private scalar name = (FieldDefinitions.forField name).Scalar

let private expectText name maximum =
    match scalar name with
    | ScalarRule.Text constraints ->
        Expect.equal constraints.Format.MinimumCharacters 1 "Permanent nonempty text"
        Expect.isNone constraints.Format.Grammar "Business text has no admission grammar"

        Expect.equal constraints.MaximumCharacters (Some maximum) "Text character bound"
    | _ -> failtest (FieldTable.key name + " must be a text scalar.")

let private textAndDateRuleTests =
    testList
        "text and date scalar catalog"
        [
            testCase "text rules retain permanent syntax and input bounds" (fun () ->
                expectText BusinessField.ClaimantName 200
                expectText BusinessField.InsurerName 200

                for name, expected in
                    [
                        BusinessField.IncidentCountry, "[A-Z]{2}"
                        BusinessField.CaseReference, "[!-~]{1,80}"
                    ] do
                    let constraints =
                        match scalar name with
                        | ScalarRule.Text value -> value
                        | _ -> failtest "Expected a text scalar"

                    Expect.equal constraints.Format.Grammar (Some expected) "Permanent syntax"

                    Expect.isNone
                        constraints.MaximumCharacters
                        "Identifiers do not have grandfatherable syntax")
            testCase "date rules retain the Gregorian input range" (fun () ->
                for name in
                    [
                        BusinessField.IncidentDate
                        BusinessField.IncidentNotificationDate
                        BusinessField.PaymentDecisionDate
                        BusinessField.PaymentDate
                    ] do
                    match scalar name with
                    | ScalarRule.CalendarDate rule ->
                        Expect.equal rule.ExactFormat "yyyy-MM-dd" "Exact format"
                        Expect.equal rule.Minimum DateOnly.MinValue "Lower bound"
                        Expect.equal rule.Maximum DateOnly.MaxValue "Upper bound"
                    | _ -> failtest (FieldTable.key name + " must be a calendar-date scalar."))
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
                    | _ -> failtest (FieldTable.key name + " must be an amount scalar.")

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
                    | _ -> failtest (FieldTable.key name + " must be a currency scalar."))
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
let tests =
    testList "scalar admission contracts" [ textAndDateRuleTests; numericAndStatusRuleTests ]
