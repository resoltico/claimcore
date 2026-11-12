module ClaimCore.Tests.RecordDeclarationTests

open ClaimCore.Application.Contracts
open ClaimCore.ContractGeneration

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json
open ClaimCore.Domain
open ClaimCore.TestSupport

let private keys group =
    match group with
    | Some value -> Inputs.fieldKeys (RecordGroup.token value)
    | None ->
        CaseFields.declarations
        |> List.filter (fun field -> field.Group.IsNone && field.Section.IsNone)
        |> List.map _.Key

let rec private members definitions =
    function
    | Schema.Ref name -> members definitions (Map.find name definitions)
    | Schema.Object(properties, _) -> properties |> List.map fst
    | Schema.OneOf([ schema; Schema.Null ], _) -> members definitions schema
    | _ -> failtest "Expected an object codec."

let private expectGroup group (codec: Codec<'T>) =
    Expect.equal
        (members codec.Definitions codec.Schema)
        (keys group)
        "Codec members follow the record declaration"

let private codecs () =
    expectGroup (Some RecordGroup.Registration) RequestCodecs.registration
    expectGroup (Some RecordGroup.Decision) RequestCodecs.decision
    expectGroup (Some RecordGroup.Payment) RequestCodecs.payment

    let actual =
        members ResponseCodecs.caseFields.Definitions ResponseCodecs.caseFields.Schema

    let expected = [ "caseReference"; "status"; "registration"; "finance" ]

    Expect.equal actual expected "The public view has complete groups"

    let properties =
        match Map.find "CaseFields" ResponseCodecs.caseFields.Definitions with
        | Schema.Object(properties, _) -> Map.ofList properties
        | _ -> failtest "Expected the grouped view"

    for group, name in [ (Some RecordGroup.Registration), "registration" ] do
        Expect.equal
            (members ResponseCodecs.caseFields.Definitions properties[name])
            (keys group)
            "Group members follow the authoritative record declaration"

    Expect.equal
        (members ResponseCodecs.caseFields.Definitions properties["finance"])
        [
            "decision"
            "paidAmount"
            "paymentCount"
            "firstPaymentDate"
            "lastPaymentDate"
        ]
        "A bounded financial position owns aggregates; payment pages and derived progress are separate"

    let renamed =
        actual
        |> List.map (fun key -> if key = "registration" then "registrationValues" else key)

    Expect.notEqual renamed expected "A renamed group is detectable"

let private domain () =
    let fields = CaseFields.declarations |> List.map _.Field
    Expect.equal (Set.ofList fields |> Set.count) fields.Length "No repeated field rows"

    Expect.equal
        (fields |> Set.ofList)
        (Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<BusinessField>)
         |> Array.map (fun case ->
             (Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, [||]) |> nonNull)
             :?> BusinessField)
         |> Set.ofArray)
        "Every business field has a table row"


let private diagnosticGroups () =
    let actual =
        InputTarget.all
        |> List.choose (function
            | InputTarget.Group group, token -> Some(group, token)
            | _ -> None)

    Expect.equal
        actual
        [ RecordGroup.Payment, "payment" ]
        "Only payment has a group diagnostic producer"

let private actGroups () =
    let expected group =
        Inputs.fieldKeys (RecordGroup.token group)
        |> List.map (fun key -> Vocabulary.tryParse BusinessField.vocabulary key |> Option.get)

    let fields inputs =
        inputs
        |> List.choose (fun (input: TaskInputDeclaration) ->
            match input.Input with
            | TaskInputId.Target(InputTarget.Field field) when field <> BusinessField.CaseReference ->
                Some field
            | _ -> None)

    for kind, group in
        [
            CaseActKind.Open, RecordGroup.Registration
            CaseActKind.AmendRegistration, RecordGroup.Registration
            CaseActKind.Decide, RecordGroup.Decision
            CaseActKind.RecordPayment, RecordGroup.Payment
        ] do
        let task = TaskDeclarations.describe (TaskId.Operation(ActKind.Case kind))

        Expect.equal
            (fields task.Inputs)
            (expected group)
            "Semantic task inputs retain the authoritative business field order"

    let correction =
        TaskDeclarations.describe (TaskId.Operation(ActKind.Case CaseActKind.Correct))

    for group in [ RecordGroup.Registration; RecordGroup.Decision ] do
        let expected = expected group

        Expect.equal
            (fields correction.Inputs
             |> List.filter (fun field -> List.contains field expected))
            expected
            "Correction retains each selected group's field order"

let private locales () =
    let catalogs = CatalogValidation.validate ()

    for language in LocalizationPolicy.languages do
        let catalog = catalogs[language]

        for row in CaseFields.declarations do
            for key in
                [
                    FieldPresentation.labelId row.Field
                    FieldPresentation.hintId row.Field
                    FieldPresentation.ruleId row.Field
                ] do
                Expect.isTrue
                    (Map.containsKey key catalog)
                    "Resolved locale has label, hint and precise rule"

[<Tests>]
let tests =
    testList
        "authoritative record declaration"
        [
            testCase "diagnostic groups admit only produced targets" diagnosticGroups
            testCase "codecs and snapshot agree" codecs
            testCase "Domain coverage and ownership agree" domain
            testCase "act and correction groups follow field-table order" actGroups
            testCase "locales cover field messages" locales
        ]
