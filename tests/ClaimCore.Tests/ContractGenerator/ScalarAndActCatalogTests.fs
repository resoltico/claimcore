module ClaimCore.Tests.ScalarAndActCatalogTests

open ClaimCore.Cli

open ClaimCore.ContractGeneration

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

let private expectFields kind expected =
    let definition = OperationCatalog.forCase kind

    match definition.Inputs with
    | ActInputShape.Fields inputs ->
        Expect.equal
            (inputs |> List.map (fun input -> input.FieldName, input.Prefill))
            expected
            "Ordered semantic inputs"
    | ActInputShape.CorrectionGroups _ -> failtest "Expected an ordered scalar input shape."

let private blank fieldName =
    InputTarget.Field fieldName, PrefillSource.Blank

let private current fieldName =
    InputTarget.Field fieldName, PrefillSource.CurrentField fieldName

let private registrationFields =
    [
        BusinessField.IncidentDate
        BusinessField.IncidentNotificationDate
        BusinessField.IncidentCountry
        BusinessField.ClaimantName
        BusinessField.InsurerName
        BusinessField.ClaimedAmount
        BusinessField.ClaimedCurrency
    ]

let private textAndDateRuleTests =
    testList
        "text and date scalar catalog"
        [
            testCase "text catalog own every text validation constraint" (fun () ->
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
            testCase "date catalog define exact Gregorian input range" (fun () ->
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
            testCase "amount and currency catalog own the existing exact grammars" (fun () ->
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
            testCase "status descriptor is the closed Domain status vocabulary" (fun () ->
                match scalar BusinessField.Status with
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

let private expectedCorrectionGroups =
    [
        (CorrectionGroup.Registration,
         [ CorrectionGroupAction.Keep; CorrectionGroupAction.Replace ],
         (registrationFields |> List.map current))
        (CorrectionGroup.Decision,
         [
             CorrectionGroupAction.Keep
             CorrectionGroupAction.Replace
             CorrectionGroupAction.Clear
         ],
         [
             current BusinessField.PaymentDecisionDate
             current BusinessField.PayableAmount
             current BusinessField.PayableCurrency
         ])
        (CorrectionGroup.Payment,
         [
             CorrectionGroupAction.Keep
             CorrectionGroupAction.Replace
             CorrectionGroupAction.Clear
         ],
         [ current BusinessField.PaymentDate ])
    ]

let private correctionGroupDetails (groups: CorrectionGroupDefinition list) =
    groups
    |> List.map (fun group ->
        group.Name,
        group.Actions,
        group.ReplaceFields
        |> List.map (fun (field: FieldInputDefinition) -> field.FieldName, field.Prefill))

let private commandInventoryTest =
    testCase "all twelve case act definitions are the sole command inventory" (fun () ->
        Expect.equal
            (OperationCatalog.caseDefinitions |> List.map (fun definition -> definition.Kind))
            CaseActKinds.all
            "Exact command order")

let private commandPrefillTest =
    testCase "command inputs have exact ordered prefill sources" (fun () ->
        expectFields
            (CaseActKind.Business BusinessActKind.Open)
            (registrationFields |> List.map blank)

        expectFields
            (CaseActKind.Business BusinessActKind.AmendRegistration)
            (registrationFields |> List.map current)

        expectFields
            (CaseActKind.Business BusinessActKind.Decide)
            [
                current BusinessField.PaymentDecisionDate
                current BusinessField.PayableAmount
                current BusinessField.PayableCurrency
            ]

        expectFields (CaseActKind.Business BusinessActKind.WithdrawDecision) []

        expectFields
            (CaseActKind.Business BusinessActKind.RecordPayment)
            [ blank BusinessField.PaymentDate ]

        expectFields (CaseActKind.Business BusinessActKind.ClearPayment) []
        expectFields (CaseActKind.Business BusinessActKind.Close) []
        expectFields (CaseActKind.Business BusinessActKind.Reopen) [])

let private correctionGroupMetadataTest =
    testCase "CORRECT describes required tagged groups and replacement fields" (fun () ->
        let definition =
            OperationCatalog.forCase (CaseActKind.Business BusinessActKind.Correct)

        match definition.Inputs with
        | ActInputShape.Fields _ -> failtest "CORRECT requires grouped inputs."
        | ActInputShape.CorrectionGroups groups ->
            Expect.equal
                (correctionGroupDetails groups)
                expectedCorrectionGroups
                "Groups, actions, and replacement scalars are semantic metadata")

let private authoredInputFields () =
    OperationCatalog.caseDefinitions
    |> List.collect (fun definition ->
        match definition.Inputs with
        | ActInputShape.Fields fields -> fields
        | ActInputShape.CorrectionGroups groups ->
            groups |> List.collect (fun group -> group.ReplaceFields))
    |> List.map (fun input -> input.FieldName)
    |> Set.ofList

let private derivedFieldsAreNotActInputsTest =
    testCase "case reference and derived status are never command inputs" (fun () ->
        let inputs = authoredInputFields ()

        Expect.isFalse
            (Set.contains (InputTarget.Field BusinessField.CaseReference) inputs)
            "Immutable target is separate"

        Expect.isFalse
            (Set.contains (InputTarget.Field BusinessField.Status) inputs)
            "Status is core-derived")

let private commandInputTests =
    testList
        "ordered command inputs and prefill metadata"
        [
            commandInventoryTest
            commandPrefillTest
            correctionGroupMetadataTest
            derivedFieldsAreNotActInputsTest
        ]

[<Tests>]
let tests =
    testList
        "Domain semantic catalog"
        [
            textAndDateRuleTests
            numericAndStatusRuleTests
            scalarOwnershipTests
            commandInputTests
        ]
