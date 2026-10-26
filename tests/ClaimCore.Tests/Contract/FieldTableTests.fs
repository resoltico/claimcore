module ClaimCore.Tests.FieldTableTests

open ClaimCore.ContractGeneration

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Contract
open ClaimCore.Json
open ClaimCore.Contract.Protocol
open ClaimCore.Domain
open ClaimCore.TestSupport

let private keys group =
    FieldTable.rows
    |> List.filter (fun row -> row.Placement = group)
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
        "Codec members follow the field table"

let private codecs () =
    expectGroup (FieldPlacement.Grouped RecordGroup.Registration) RequestCodecs.registration
    expectGroup (FieldPlacement.Grouped RecordGroup.Decision) RequestCodecs.decision
    expectGroup (FieldPlacement.Grouped RecordGroup.Payment) RequestCodecs.payment

    let actual =
        members ResponseCodecs.caseFields.Definitions ResponseCodecs.caseFields.Schema

    let expected = [ "caseReference"; "status"; "registration"; "decision"; "payment" ]
    Expect.equal actual expected "The public view has complete groups"

    let properties =
        match Map.find "CaseFields" ResponseCodecs.caseFields.Definitions with
        | Schema.Object(properties, _) -> Map.ofList properties
        | _ -> failtest "Expected the grouped view"

    for group, name in
        [
            (FieldPlacement.Grouped RecordGroup.Registration), "registration"
            (FieldPlacement.Grouped RecordGroup.Decision), "decision"
            (FieldPlacement.Grouped RecordGroup.Payment), "payment"
        ] do
        Expect.equal
            (members ResponseCodecs.caseFields.Definitions properties[name])
            (keys group)
            "Group members follow the authoritative field table"

    let renamed =
        actual
        |> List.map (fun key -> if key = "registration" then "registrationValues" else key)

    Expect.notEqual renamed expected "A renamed group is detectable"

let private domain () =
    let fields = FieldTable.rows |> List.map _.Field
    Expect.equal (Set.ofList fields |> Set.count) fields.Length "No repeated field rows"

    Expect.equal
        (fields |> Set.ofList)
        (Microsoft.FSharp.Reflection.FSharpType.GetUnionCases(typeof<BusinessField>)
         |> Array.map (fun case ->
             (Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, [||]) |> nonNull)
             :?> BusinessField)
         |> Set.ofArray)
        "Every business field has a table row"

    let retained =
        FieldTable.rows |> List.filter _.RetainedOnErasure |> List.map _.Field

    Expect.equal retained [ BusinessField.CaseReference ] "Only the reference survives erasure"

let private diagnosticGroups () =
    let actual =
        InputTargets.all
        |> List.choose (function
            | InputTarget.Group group, token -> Some(group, token)
            | _ -> None)

    Expect.equal
        actual
        [ RecordGroup.Payment, "payment" ]
        "Only payment has a group diagnostic producer"

let private actGroups () =
    let expected group =
        FieldTable.rows
        |> List.filter (fun row -> row.Placement = group)
        |> List.map (fun row -> InputTarget.Field row.Field)

    let actual kind =
        match (OperationCatalog.forCase (CaseActKind.Business kind)).Inputs with
        | ActInputShape.Fields fields -> fields |> List.map _.FieldName
        | _ -> failtest "Expected flat act fields"

    for act, group in
        [
            BusinessActKind.Open, (FieldPlacement.Grouped RecordGroup.Registration)
            BusinessActKind.AmendRegistration, (FieldPlacement.Grouped RecordGroup.Registration)
            BusinessActKind.Decide, (FieldPlacement.Grouped RecordGroup.Decision)
            BusinessActKind.RecordPayment, (FieldPlacement.Grouped RecordGroup.Payment)
        ] do
        Expect.equal (actual act) (expected group) "Act fields follow their source group's order"

    let correction =
        OperationCatalog.forCase (CaseActKind.Business BusinessActKind.Correct)

    match correction.Inputs with
    | ActInputShape.CorrectionGroups groups ->
        for group in groups do
            let fields = group.ReplaceFields |> List.map _.FieldName

            let kind = FieldPlacement.Grouped group.Name

            Expect.equal
                fields
                (expected kind)
                "Correction replacements use the same field group order"
    | _ -> failtest "Expected grouped correction fields"

    Expect.notEqual
        (actual BusinessActKind.Open |> List.rev)
        (expected (FieldPlacement.Grouped RecordGroup.Registration))
        "A reordered dependent inventory is detectable"

let private locales () =
    let directory =
        Path.Combine(RepositoryRoot.find (), "src/ClaimCore.Contract/Localization")

    for language in LocalizationPolicy.languages do
        use document =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, language + ".json")))

        for row in FieldTable.rows do
            for key in [ FieldTable.labelId row.Field; FieldTable.meaningId row.Field ] do
                Expect.isTrue
                    (document.RootElement.TryGetProperty key |> fst)
                    "Locale has both field messages"

[<Tests>]
let tests =
    testList
        "authoritative field table"
        [
            testCase "diagnostic groups admit only produced targets" diagnosticGroups
            testCase "codecs and snapshot agree" codecs
            testCase "Domain coverage and ownership agree" domain
            testCase "act and correction groups follow field-table order" actGroups
            testCase "locales cover field messages" locales
        ]
