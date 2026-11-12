module ClaimCore.Tests.WireBoundaryProperties

open System
open Expecto
open Hedgehog
open Hedgehog.FSharp
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json
open ClaimCore.Wire
open ClaimCore.TestSupport
open ClaimCore.Tests.PropertyHarness

let private admitted result =
    Result.defaultWith (fun error -> failtestf "Wire property fixture: %A" error) result

let private text =
    Gen.choice
        [
            Gen.string (Range.constant 0 80) Gen.alpha
            Gen.item [ "Žąsis"; "東京"; "😀"; "é"; "مرحبا"; "\"quoted\\text" ]
        ]

let private authoredIntent =
    property {
        let! name = text
        let! amount = Gen.item [ "12.3400"; "01"; "1e0"; ""; "999999999999999999.9999" ]
        let! revision = Gen.int64 (Range.constant 1L Revision.Maximum)

        let operation =
            OperationId.create (Guid.Parse "00000000-0000-7000-8000-000000000001")
            |> Option.get

        let basis =
            {
                CaseTaskBasis.Operation = operation
                Target = CaseSelector.Reference "SPELLED-Target"
                Revision = Revision.create revision |> Option.get
            }

        let command =
            TaskAssembly.amendRegistration
                basis
                { RegistrationAmendmentDraft.empty with
                    ClaimantName = TaskUpdate.Set name
                    IncidentDescription = OptionalTaskUpdate.Clear
                    Claimed = TaskUpdate.Set { Amount = amount; Currency = "EUR" }
                }
            |> admitted

        let bytes = Codec.encode RequestCodecs.operationInput command

        return
            Codec.decode Limits.MaximumRequestBytes RequestCodecs.operationInput bytes = Ok command
    }

let private total (codec: Codec<'value>) maximum samples =
    Expect.isNonEmpty samples "Every exercised boundary has a valid control"

    for sample in samples do
        Expect.isOk
            (Codec.decode maximum codec sample)
            "The unmutated typed boundary control admits"

    let generator =
        Gen.choice (DecoderCorpora.jsonLike :: (samples |> List.map DecoderCorpora.mutated))

    property {
        let! raw = generator

        return
            try
                Codec.decode maximum codec raw |> ignore
                true
            with _ ->
                false
    }

let private propertyFor response (definition: IEndpointDefinition) =
    definition.Accept
        { new IEndpointVisitor<Property<bool>> with
            member _.Visit<'input, 'output>(endpoint: Endpoint<'input, 'output>) =
                if response then
                    total
                        endpoint.Output
                        Limits.MaximumResponseBytes
                        (ResponseExamples.samples endpoint.Token)
                else
                    let seed = RequestInputs.forEndpoint endpoint.Token |> WireWriter.bytes
                    total endpoint.Input Limits.MaximumRequestBytes [ seed ]
        }

[<Tests>]
let tests =
    testList
        "typed transport boundary properties"
        [
            testCase
                "semantic task acquisition preserves selected empty clear and malformed authored scalars through Wire"
                (fun () -> run "semantic task scalar preservation" authoredIntent)
            for definition in Api.definitions do
                let endpoint = Route.ofDefinition definition

                testCase
                    (Api.id endpoint + " request decoder is total over hostile bytes")
                    (fun () ->
                        run (Api.id endpoint + " request totality") (propertyFor false definition))

                testCase (Api.id endpoint + " reply decoder is total over hostile bytes") (fun () ->
                    run (Api.id endpoint + " reply totality") (propertyFor true definition))
        ]
