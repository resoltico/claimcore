module ClaimCore.Tests.RecordDeclarationTests

open ClaimCore.ProtocolGeneration

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Protocol
open ClaimCore.Json
open ClaimCore.Domain
open ClaimCore.TestSupport

let private keys group =
    match group with
    | Some value -> Inventory.fieldKeys (RecordGroup.token value)
    | None ->
        Inventory.rows "specialFields"
        |> List.map (fun row -> row.GetProperty("key").GetString() |> nonNull)

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

    let expected =
        [
            "caseReference"
            "status"
            "registration"
            "decision"
            "payment"
            "work"
            "progress"
        ]

    Expect.equal actual expected "The public view has complete groups"

    let properties =
        match Map.find "CaseFields" ResponseCodecs.caseFields.Definitions with
        | Schema.Object(properties, _) -> Map.ofList properties
        | _ -> failtest "Expected the grouped view"

    for group, name in
        [
            (Some RecordGroup.Registration), "registration"
            (Some RecordGroup.Decision), "decision"
            (Some RecordGroup.Payment), "payment"
        ] do
        Expect.equal
            (members ResponseCodecs.caseFields.Definitions properties[name])
            (keys group)
            "Group members follow the authoritative record declaration"

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
        CaseFields.declarations
        |> List.filter (fun row -> row.Group = Some group)
        |> List.map _.Field

    let entry (kind: CatalogOperation) =
        Catalog.document.Entries
        |> List.find (fun entry -> entry.Endpoint = Api.operationExecute.Token && entry.Kind = kind)

    let fields inputs =
        inputs
        |> List.choose (fun (input: CatalogInput) ->
            match input.Field with
            | Some(InputTarget.Field field) when field <> BusinessField.CaseReference -> Some field
            | _ -> None)

    for kind, group in
        [
            CatalogOperation.Act(ActKind.Case CaseActKind.Open), RecordGroup.Registration
            CatalogOperation.Act(ActKind.Case CaseActKind.AmendRegistration),
            RecordGroup.Registration
            CatalogOperation.Act(ActKind.Case CaseActKind.Decide), RecordGroup.Decision
            CatalogOperation.Act(ActKind.Case CaseActKind.RecordPayment), RecordGroup.Payment
        ] do
        Expect.equal
            (fields (entry kind).Inputs)
            (expected group)
            "Declared act fields retain the authoritative order"

    for group in Vocabulary.all RecordGroup.vocabulary do
        let inputs =
            (entry (CatalogOperation.Act(ActKind.Case CaseActKind.Correct))).Inputs
            |> List.filter (fun input ->
                List.truncate 3 input.Path = [ "act"; "groups"; RecordGroup.token group ])

        Expect.equal
            (fields inputs)
            (expected group)
            "Correction replacements retain their field order"

let private locales () =
    let directory =
        Path.Combine(RepositoryRoot.find (), "src/ClaimCore.Protocol/Messages/Localization")

    for language in LocalizationPolicy.languages do
        use document =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, language + ".json")))

        for row in CaseFields.declarations do
            for key in
                [ FieldPresentation.labelId row.Field; FieldPresentation.meaningId row.Field ] do
                Expect.isTrue
                    (document.RootElement.TryGetProperty key |> fst)
                    "Locale has both field messages"

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
