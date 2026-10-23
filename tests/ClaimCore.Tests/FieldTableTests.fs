module ClaimCore.Tests.FieldTableTests

open System
open System.IO
open System.Text.Json
open Expecto
open ClaimCore.Contract
open ClaimCore.Contract.Protocol
open ClaimCore.Domain
open ClaimCore.TestSupport

let private keys group =
    FieldTable.rows
    |> List.filter (fun row -> row.Group = group)
    |> List.map _.Key
    |> Set.ofList

let rec private members definitions =
    function
    | Schema.Ref name -> members definitions (Map.find name definitions)
    | Schema.Object(properties, _) -> properties |> List.map fst |> Set.ofList
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

    let expected = FieldTable.rows |> List.map _.Key |> Set.ofList
    Expect.equal actual expected "Snapshot business fields follow the field table"
    let renamed = actual |> Set.remove "incidentDate" |> Set.add "incidentDay"
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

let private locales () =
    let directory =
        Path.Combine(RepositoryRoot.find (), "src/ClaimCore.Contract/Localization")

    for row in FieldTable.rows do
        for key in [ FieldTable.labelId row.Field; FieldTable.meaningId row.Field ] do
            Expect.isFalse
                (String.IsNullOrEmpty(MessageText.literal key))
                "English field message exists"

    for language in [ "lv"; "ar" ] do
        use document =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, language + ".field.json")))

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
            testCase "locales cover field messages" locales
        ]
