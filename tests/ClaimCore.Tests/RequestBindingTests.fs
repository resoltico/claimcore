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
    let definitions =
        OpenApi.definitions ()
        |> Map.add "CaseAct" (Schema.OneOf([ Schema.Ref "CaseAct.OPEN" ], None))

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

let private editObject name edit definitions =
    match Map.find name definitions with
    | Schema.Object(members, optional) ->
        Map.add name (Schema.Object(edit members, optional)) definitions
    | _ -> failtest "Expected an object fixture"

let private publicationRefusal definitions commands =
    let directory =
        IO.Directory.CreateTempSubdirectory("claimcore-binding-publication-")

    let original = [| 123uy; 125uy |]
    let path = IO.Path.Combine(directory.FullName, "request-bindings.json")
    IO.File.WriteAllBytes(path, original)

    try
        Expect.throws
            (fun () ->
                let artifact =
                    {
                        Name = "request-bindings.json"
                        Bytes = RequestBindings.renderFrom definitions commands
                    }

                ArtifactPublication.publish directory.FullName [ artifact ])
            "Unsupported shape must refuse publication"

        Expect.equal (IO.File.ReadAllBytes path) original "Refusal preserves the existing artifact"
    finally
        directory.Delete true

[<Tests>]
let shapeTests =
    testCase
        "publication accounts for required literals, all choices and command discriminators"
        (fun () ->
            let definitions = OpenApi.definitions ()

            let constant =
                editObject
                    "Registration"
                    (fun fields -> fields @ [ "source", Schema.Const "SYNTHETIC" ])
                    definitions

            let keep =
                editObject
                    "RegistrationGroup.KEEP"
                    (fun fields -> fields @ [ "reason", Schema.String(None, None, None, None) ])
                    definitions

            let kind =
                editObject
                    "CaseAct.OPEN"
                    (List.map (fun (name, value) ->
                        name, if name = "kind" then Schema.Const "DIFFERENT" else value))
                    definitions

            let groupedExtra =
                editObject
                    "CaseAct.CORRECT_CASE"
                    (fun fields -> fields @ [ "source", Schema.Const "SYNTHETIC" ])
                    definitions

            let empty =
                editObject
                    "Registration"
                    (fun fields -> fields @ [ "extra", Schema.Object([], []) ])
                    definitions

            for poisoned in [ constant; keep; kind; groupedExtra; empty ] do
                publicationRefusal poisoned CommandDefinitions.all)


[<Tests>]
let identityTests =
    testCase
        "emitted discovery uses the declared retargeted identities and their English text"
        (fun () ->
            use fixture =
                JsonDocument.Parse(ReadOnlyMemory<byte>(EvolutionQualification.render ()))

            let descriptors = fixture.RootElement.GetProperty "descriptors"

            let command =
                descriptors.GetProperty("commands").EnumerateArray()
                |> Seq.find (fun command -> command.GetProperty("kind").GetString() = "OPEN")

            for property, identity in
                [ "label", "command.CLOSE.label"; "meaning", "command.CLOSE.meaning" ] do
                Expect.equal
                    (command.GetProperty(property + "Id").GetString())
                    identity
                    "The declared identity survives projection"

                Expect.equal
                    (command.GetProperty(property).GetString())
                    (MessageText.literal identity)
                    "English renders the same identity"

            let target = descriptors.GetProperty "authorityTarget"

            Expect.equal
                (target.GetProperty("labelId").GetString())
                "authority.field.subject.label"
                "Principal input identity survives projection"

            let role =
                descriptors.GetProperty("authorityFields").EnumerateArray()
                |> Seq.find (fun field -> field.GetProperty("name").GetString() = "role")

            Expect.equal
                (role.GetProperty("labelId").GetString())
                "authority.field.scope.label"
                "Protocol labels preserve the core input identity")
