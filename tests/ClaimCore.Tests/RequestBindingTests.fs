module ClaimCore.Tests.RequestBindingTests

open System
open System.Text.Json
open Expecto
open ClaimCore.Catalog
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.ContractGeneration

let private opening =
    CommandDefinitions.forKind (CaseActKind.Business BusinessActKind.Open)

let private addReason (command: CommandDefinition) =
    match command.Inputs with
    | CommandInputShape.Fields fields ->
        { command with
            Inputs =
                CommandInputShape.Fields(
                    fields
                    @ [
                        {
                            FieldName = InputField.Reason
                            Prefill = PrefillSource.Blank
                        }
                    ]
                )
        }
    | _ -> failtest "Fixture requires flat inputs"

let private changedCodec () =
    let definitions = OpenApi.definitions ()

    match Map.find "Registration" definitions with
    | Schema.Object(fields, optional) ->
        Map.add
            "Registration"
            (Schema.Object(fields @ [ "reason", Schema.String(None, None, None, None) ], optional))
            definitions
    | _ -> failtest "Fixture requires a registration object"

let private propagation () =
    let emitted = RequestBindings.renderFrom (changedCodec ()) [ addReason opening ]
    use document = JsonDocument.Parse(ReadOnlyMemory<byte> emitted)

    let binding =
        document.RootElement[0].GetProperty("fields").EnumerateArray()
        |> Seq.find (fun field -> field.GetProperty("fieldName").GetString() = "reason")

    Expect.equal
        (binding.GetProperty("path").EnumerateArray()
         |> Seq.map _.GetString()
         |> Seq.toList)
        [ "values"; "reason" ]
        "Synthetic input propagates to its codec-owned wire member"

[<Tests>]
let tests =
    testList
        "core request binding qualification"
        [
            testCase
                "a fixture-only input change propagates without client command rules"
                propagation
            testCase "a codec member missing from semantic inputs refuses" (fun () ->
                Expect.throws
                    (fun () -> RequestBindings.renderFrom (changedCodec ()) [ opening ] |> ignore)
                    "Unbound fields refuse")
            testCase "a semantic input missing from the codec refuses" (fun () ->
                Expect.throws
                    (fun () ->
                        RequestBindings.renderFrom (OpenApi.definitions ()) [ addReason opening ]
                        |> ignore)
                    "Unknown inputs refuse")
            testCase "duplicate authored core messages refuse" (fun () ->
                Expect.throws
                    (fun () ->
                        MessageText.admitDefinitions [ "duplicate", []; "duplicate", [] ]
                        |> ignore)
                    "Duplicate identity cannot overwrite a message")
            testCase "unknown confirmation identities refuse" (fun () ->
                Expect.throws
                    (fun () ->
                        CatalogValidation.validateIdentity (Set.singleton "known") "missing")
                    "Unknown core messages refuse")
        ]
