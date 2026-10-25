module ClaimCore.Tests.FieldTableTests

open ClaimCore.ContractGeneration

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Contract
open ClaimCore.Contract.Protocol
open ClaimCore.Domain
open ClaimCore.TestSupport

let private keys group =
    FieldTable.rows |> List.filter (fun row -> row.Group = group) |> List.map _.Key

let rec private members definitions =
    function
    | Schema.Ref name -> members definitions (Map.find name definitions)
    | Schema.Object(properties, _) -> properties |> List.map fst
    | _ -> failtest "Expected an object codec."

let private expectGroup group (codec: Codec<'T>) =
    Expect.equal
        (members codec.Definitions codec.Schema)
        (keys group)
        "Codec members follow the field table"

let private codecs () =
    expectGroup FieldGroup.Registration RequestCodecs.registration
    expectGroup FieldGroup.Decision RequestCodecs.decision
    expectGroup FieldGroup.Payment RequestCodecs.payment

    let actual =
        members ResponseCodecs.caseFields.Definitions ResponseCodecs.caseFields.Schema

    let expected = FieldTable.rows |> List.map _.Key
    Expect.equal actual expected "Snapshot business fields follow the field table"

    let renamed =
        actual
        |> List.map (fun key -> if key = "incidentDate" then "incidentDay" else key)

    Expect.notEqual renamed expected "The comparison detects a renamed codec member"

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

    Expect.equal
        (FieldDefinitions.all |> List.map _.Field)
        (FieldTable.rows |> List.map _.Field)
        "Every field has one admission definition in table order"

    Expect.equal
        (typeof<FieldDefinition>.GetProperties() |> Array.map _.Name |> Set.ofArray)
        (set [ "Field"; "Scalar" ])
        "Domain does not duplicate presentation metadata"

let private commandGroups () =
    let expected group =
        FieldTable.rows
        |> List.filter (fun row -> row.Group = group)
        |> List.map (fun row -> InputTarget.Field row.Field)

    let actual kind =
        match (ActPresentation.forKind (CaseActKind.Business kind)).Inputs with
        | ActInputShape.Fields fields -> fields |> List.map _.FieldName
        | _ -> failtest "Expected flat command fields"

    for act, group in
        [
            BusinessActKind.Open, FieldGroup.Registration
            BusinessActKind.AmendRegistration, FieldGroup.Registration
            BusinessActKind.Decide, FieldGroup.Decision
            BusinessActKind.RecordPayment, FieldGroup.Payment
        ] do
        Expect.equal
            (actual act)
            (expected group)
            "Command fields follow their source group's order"

    let correction =
        ActPresentation.forKind (CaseActKind.Business BusinessActKind.Correct)

    match correction.Inputs with
    | ActInputShape.CorrectionGroups groups ->
        for group in groups do
            let fields = group.ReplaceFields |> List.map _.FieldName

            let kind =
                match group.Name with
                | CorrectionGroup.Registration -> FieldGroup.Registration
                | CorrectionGroup.Decision -> FieldGroup.Decision
                | CorrectionGroup.Payment -> FieldGroup.Payment

            Expect.equal
                fields
                (expected kind)
                "Correction replacements use the same field group order"
    | _ -> failtest "Expected grouped correction fields"

    Expect.notEqual
        (actual BusinessActKind.Open |> List.rev)
        (expected FieldGroup.Registration)
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
            testCase "codecs and snapshot agree" codecs
            testCase "Domain coverage and ownership agree" domain
            testCase "command and correction groups follow field-table order" commandGroups
            testCase "locales cover field messages" locales
        ]
