module ClaimCore.Tests.DomainDescriptorTests

open System
open Expecto
open ClaimCore.Domain

let private scalar name = (FieldDefinitions.forName name).Scalar

let private expectText name maximum =
    match scalar name with
    | ScalarRule.Text constraints ->
        Expect.equal constraints.MinimumCharacters 1 "Minimum characters"
        Expect.equal constraints.MaximumCharacters maximum "Maximum characters"
        Expect.isTrue constraints.RequiresNonBlank "Non-blank input"
        Expect.isTrue constraints.RejectsSurroundingWhitespace "No surrounding whitespace"
        Expect.isTrue constraints.RejectsControlCharacters "No control characters"
        Expect.isTrue constraints.RequiresWellFormedUnicode "Well-formed Unicode"
    | _ -> failtest (name + " must be a text scalar.")

let private expectInput kind expected =
    let definition = CommandDefinitions.forKind kind

    Expect.equal
        (definition.Inputs |> List.map (fun input -> input.FieldName, input.Prefill))
        expected
        "Ordered semantic inputs"

let private textAndDateRuleTests =
    testList
        "text and date scalar descriptors"
        [
            testCase "text descriptors own every text validation constraint" (fun () ->
                expectText "incidentCountry" 100
                expectText "claimantName" 200
                expectText "insurerName" 200
                expectText "caseReference" 80)
            testCase "date descriptors define exact Gregorian input range" (fun () ->
                for name in
                    [
                        "incidentDate"
                        "incidentNotificationDate"
                        "paymentDecisionDate"
                        "paymentDate"
                    ] do
                    match scalar name with
                    | ScalarRule.CalendarDate rule ->
                        Expect.equal rule.ExactFormat "yyyy-MM-dd" "Exact format"
                        Expect.equal rule.Minimum DateOnly.MinValue "Lower bound"
                        Expect.equal rule.Maximum DateOnly.MaxValue "Upper bound"
                    | _ -> failtest (name + " must be a calendar-date scalar."))
        ]

let private numericAndStatusRuleTests =
    testList
        "numeric and status scalar descriptors"
        [
            testCase "amount and currency descriptors own the existing exact grammars" (fun () ->
                for name in [ "claimedAmount"; "payableAmount" ] do
                    match scalar name with
                    | ScalarRule.Amount rule ->
                        Expect.equal rule.Text.MaximumCharacters 23 "Amount byte-safe text bound"
                        Expect.equal rule.Grammar "(0|[1-9][0-9]{0,17})(\\.[0-9]{1,4})?" "Grammar"
                        Expect.equal rule.MaximumIntegerDigits 18 "Whole-digit bound"
                        Expect.equal rule.MaximumFractionalDigits 4 "Fractional-digit bound"
                    | _ -> failtest (name + " must be an amount scalar.")

                for name in [ "claimedCurrency"; "payableCurrency" ] do
                    match scalar name with
                    | ScalarRule.Currency rule ->
                        Expect.equal rule.Text.MaximumCharacters 3 "Currency text bound"
                        Expect.equal rule.Grammar "[A-Z]{3}" "Grammar"
                        Expect.equal rule.ExactCharacters 3 "Exact length"
                    | _ -> failtest (name + " must be a currency scalar."))
            testCase "status descriptor is the closed Domain status vocabulary" (fun () ->
                match scalar "status" with
                | ScalarRule.CaseStatus rule ->
                    Expect.equal rule.AllowedValues CaseStatuses.all "No renderer-owned status"
                | _ -> failtest "status must be a case-status scalar.")
        ]

let private scalarOwnershipTests =
    testList
        "scalar metadata ownership"
        [
            testCase "field definitions expose scalar rules without a duplicate kind" (fun () ->
                let propertyNames =
                    typeof<FieldDefinition>.GetProperties()
                    |> Array.map (fun property -> property.Name)
                    |> Set.ofArray

                Expect.isFalse
                    (Set.contains "Kind" propertyNames)
                    "Scalar is the sole field-shape descriptor"

                Expect.isTrue
                    (Set.contains "Scalar" propertyNames)
                    "Domain scalar metadata remains public")
        ]

let private commandInputTests =
    testList
        "ordered command inputs and prefill metadata"
        [
            testCase "all eight command definitions are the sole command inventory" (fun () ->
                Expect.equal
                    (CommandDefinitions.all |> List.map (fun definition -> definition.Kind))
                    CommandKinds.all
                    "Exact command order")
            testCase "command inputs have exact ordered prefill sources" (fun () ->
                let blank fieldName = fieldName, PrefillSource.Blank

                let current fieldName =
                    fieldName, PrefillSource.CurrentField fieldName

                let registration =
                    [
                        "incidentDate"
                        "incidentNotificationDate"
                        "incidentCountry"
                        "claimantName"
                        "insurerName"
                        "claimedAmount"
                        "claimedCurrency"
                    ]

                expectInput CommandKind.Open (registration |> List.map blank)
                expectInput CommandKind.AmendRegistration (registration |> List.map current)

                expectInput
                    CommandKind.Decide
                    [
                        current "paymentDecisionDate"
                        current "payableAmount"
                        current "payableCurrency"
                    ]

                expectInput CommandKind.WithdrawDecision []
                expectInput CommandKind.RecordPayment [ blank "paymentDate" ]
                expectInput CommandKind.ClearPayment []
                expectInput CommandKind.Close []
                expectInput CommandKind.Reopen [])
            testCase "case reference and derived status are never command inputs" (fun () ->
                let inputs =
                    CommandDefinitions.all
                    |> List.collect (fun definition -> definition.Inputs)
                    |> List.map (fun input -> input.FieldName)
                    |> Set.ofList

                Expect.isFalse (Set.contains "caseReference" inputs) "Immutable target is separate"
                Expect.isFalse (Set.contains "status" inputs) "Status is core-derived")
        ]

let private ruleInventoryTests =
    testList
        "stable executable-rule inventory"
        [
            testCase "cross-field and transition rules have unique stable identifiers" (fun () ->
                let identifiers = DomainRules.all |> List.map (fun rule -> rule.Identifier)

                let expected =
                    [
                        "IMMUTABLE_CASE_REFERENCE"
                        "EVENT_DATE_ORDER"
                        "EVENT_DATE_NOT_FUTURE"
                        "DECISION_TUPLE_COMPLETE"
                        "PAYMENT_REQUIRES_POSITIVE_DECISION"
                        "OPEN_REQUIRES_ABSENT_CASE"
                        "OPEN_REQUIRES_ZERO_EXPECTED_REVISION"
                        "MUTATION_REQUIRES_CURRENT_CASE"
                        "EXPECTED_REVISION_REVALIDATED"
                        "CLOSED_CASE_REOPEN_FIRST"
                        "AMENDMENT_REQUIRES_UNDECIDED_CASE"
                        "DECISION_REQUIRES_UNPAID_CASE"
                        "WITHDRAWAL_REQUIRES_UNPAID_DECISION"
                        "PAYMENT_REQUIRES_UNPAID_DECISION"
                        "CLEAR_PAYMENT_REQUIRES_RECORDED_PAYMENT"
                        "CLOSE_REQUIRES_OPEN_CASE"
                        "REOPEN_REQUIRES_CLOSED_CASE"
                    ]

                Expect.equal identifiers expected "Exact rule inventory"

                Expect.contains
                    (DomainRules.all |> List.map (fun rule -> rule.Category))
                    DomainRuleCategory.CrossField
                    "Cross-field rules"

                Expect.contains
                    (DomainRules.all |> List.map (fun rule -> rule.Category))
                    DomainRuleCategory.Transition
                    "Transition rules")
        ]

let tests =
    testList
        "Domain semantic descriptors"
        [
            textAndDateRuleTests
            numericAndStatusRuleTests
            scalarOwnershipTests
            commandInputTests
            ruleInventoryTests
        ]
