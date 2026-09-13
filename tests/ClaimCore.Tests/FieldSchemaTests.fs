module ClaimCore.Tests.FieldSchemaTests

open System.Text.Json
open Expecto
open ClaimCore.Contracts
open ClaimCore.Domain

let private expectedFields =
    [
        "incidentDate"
        "incidentNotificationDate"
        "incidentCountry"
        "claimantName"
        "insurerName"
        "claimedAmount"
        "claimedCurrency"
        "caseReference"
        "paymentDecisionDate"
        "payableAmount"
        "payableCurrency"
        "paymentDate"
        "status"
    ]

let private semanticDocument () =
    ContractProjection.current ()
    |> ContractRenderers.semantic
    |> CanonicalContract.bytes
    |> fun bytes -> JsonDocument.Parse(System.ReadOnlyMemory<byte>(bytes))

let private fieldNames (document: JsonDocument) =
    document.RootElement.GetProperty("fields").EnumerateArray()
    |> Seq.map (fun field -> field.GetProperty("name").GetString())
    |> Seq.toList

let private semanticFieldTests =
    testList
        "semantic field schema"
        [
            testCase
                "semantic contract renders exactly thirteen ordered field descriptors"
                (fun () ->
                    use document = semanticDocument ()
                    Expect.equal (fieldNames document) expectedFields "Closed business record")
            testCase "semantic scalar metadata originates from Domain descriptors" (fun () ->
                use document = semanticDocument ()
                let fields = document.RootElement.GetProperty("fields")

                for definition in FieldDefinitions.all do
                    let rendered =
                        fields.EnumerateArray()
                        |> Seq.find (fun field ->
                            field.GetProperty("name").GetString() = definition.Name)

                    Expect.equal
                        (rendered.GetProperty("label").GetString())
                        definition.Label
                        ("Label: " + definition.Name)

                    Expect.equal
                        (rendered.GetProperty("meaning").GetString())
                        definition.Meaning
                        ("Meaning: " + definition.Name))
        ]

let private commandMetadataTests =
    testList
        "semantic command metadata"
        [
            testCase "command descriptors are the only ordered input inventory" (fun () ->
                use document = semanticDocument ()

                let rendered =
                    document.RootElement.GetProperty("commands").EnumerateArray() |> Seq.toList

                Expect.equal
                    rendered.Length
                    CommandDefinitions.all.Length
                    "No duplicate command registry"

                for definition in CommandDefinitions.all do
                    let command =
                        rendered
                        |> List.find (fun item ->
                            item.GetProperty("kind").GetString() = CommandKinds.token
                                definition.Kind)

                    let inputs =
                        command.GetProperty("inputs").EnumerateArray()
                        |> Seq.map (fun item -> item.GetProperty("fieldName").GetString())
                        |> Seq.toList

                    Expect.equal
                        inputs
                        (definition.Inputs |> List.map (fun input -> input.FieldName))
                        ("Ordered inputs: " + CommandKinds.token definition.Kind)

                    Expect.isFalse
                        (inputs |> List.contains "caseReference")
                        "Immutable target is not an authored command value")
        ]

let tests =
    testList "generated semantic field contract" [ semanticFieldTests; commandMetadataTests ]
