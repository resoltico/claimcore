module ClaimCore.Tests.CatalogBindingTests

open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Json

let private memberKey = "Input", "value"

let private annotation =
    {
        Name = InteractionInput.Target InputTarget.Reason
        Source = InputSource.Authored
        Prefill = None
        Default = None
        Implicit = None
        Fixed = None
    }

let private scalar = Schema.String(None, None, None, None)

let private definitions =
    Map.ofList [ "Input", Schema.Object([ "value", scalar ], []) ]

let private nodes () =
    Bindings.walk definitions Map.empty (Schema.Ref "Input")

[<Tests>]
let tests =
    testList
        "catalog schema derivation controls"
        [
            testCase "a named scalar derives its path kind and wire-required state" (fun () ->
                let inputs, constants =
                    Bindings.bind (Map.ofList [ memberKey, annotation ]) (nodes ())

                Expect.isEmpty constants "An authored scalar creates no constant"
                Expect.hasLength inputs 1 "One schema leaf creates one binding"

                Expect.equal
                    inputs.Head.Binding.Path
                    [ "value" ]
                    "Path comes from the schema member"

                Expect.equal
                    inputs.Head.Binding.Kind
                    BindingKind.Text
                    "Kind comes from the scalar schema"

                Expect.isTrue
                    inputs.Head.Binding.Required
                    "Required is structural absent interaction policy")
            testCase
                "finite choices derive vocabulary from the codec and refuse missing identities"
                (fun () ->
                    let field codec =
                        Codec.record id |> Codec.field "unfamiliar" id codec |> Codec.build

                    let described = field (VocabularyCodec.create Role.vocabulary)

                    match Bindings.walk described.Definitions Map.empty described.Schema with
                    | [ BindingNode.Input input ] ->
                        match input.Kind with
                        | BindingKind.Choice(name, choices) ->
                            Expect.equal
                                name
                                "role"
                                "The codec carries its Domain vocabulary identity"

                            Expect.contains choices "CASE_READER" "The actual options are retained"

                            Expect.equal
                                input.Path
                                [ "unfamiliar" ]
                                "Vocabulary does not depend on a known path"
                        | _ -> failtest "Expected a finite choice"
                    | _ -> failtest "Expected one described input"

                    let unlabelled = field (Codec.enumeration [ 1, "ONE"; 2, "TWO" ])

                    Expect.throws
                        (fun () ->
                            Bindings.walk unlabelled.Definitions Map.empty unlabelled.Schema
                            |> ignore)
                        "An unlabelled enumeration cannot become a catalog choice"

                    let tagged =
                        Codec.tagged
                            "Unlabelled"
                            [ Codec.unitVariant "ONE" 1; Codec.unitVariant "TWO" 2 ]

                    Expect.throws
                        (fun () ->
                            Bindings.walk
                                tagged.Definitions
                                (Map.ofList [ "Unlabelled", "ONE" ])
                                tagged.Schema
                            |> ignore)
                        "Selecting a branch cannot hide its missing vocabulary")
            testCase "an unannotated authored input fails derivation" (fun () ->
                Expect.throws
                    (fun () -> Bindings.bind Map.empty (nodes ()) |> ignore)
                    "The same binder refuses a missing authored annotation")
            testCase "an orphan annotation fails derivation" (fun () ->
                let annotations =
                    Map.ofList [ memberKey, annotation; ("Input", "misspelled"), annotation ]

                Expect.throws
                    (fun () -> Bindings.bind annotations (nodes ()) |> ignore)
                    "An annotation cannot manufacture a nonexistent member")
            testCase "an undeclared selected branch token fails the schema walk" (fun () ->
                let definitions =
                    Map.ofList
                        [
                            "Input",
                            Schema.OneOf(
                                [ Schema.Ref "Allowed" ],
                                Some("kind", [ "ALLOWED", "Allowed" ])
                            )
                            "Allowed",
                            Schema.Object([ "kind", Schema.Const "ALLOWED"; "value", scalar ], [])
                        ]

                let allowed =
                    Bindings.walk
                        definitions
                        (Map.ofList [ "Input", "ALLOWED" ])
                        (Schema.Vocabulary("synthetic", Schema.Ref "Input"))

                Expect.hasLength
                    allowed
                    2
                    "The declared branch admits with vocabulary and its scalar"

                Expect.throws
                    (fun () ->
                        Bindings.walk
                            definitions
                            (Map.ofList [ "Input", "UNDECLARED" ])
                            (Schema.Vocabulary("synthetic", Schema.Ref "Input"))
                        |> ignore)
                    "Branch selection must use a token declared by the codec")
            testCase "a fixed value is checked against the codec integer bounds" (fun () ->
                let definitions =
                    Map.ofList [ "Input", Schema.Object([ "value", Schema.Integer(1L, 10L) ], []) ]

                let nodes = Bindings.walk definitions Map.empty (Schema.Ref "Input")

                let annotations =
                    Map.ofList
                        [
                            memberKey,
                            { annotation with
                                Fixed = Some(BindingValue.Integer 0L)
                            }
                        ]

                Expect.throws
                    (fun () -> Bindings.bind annotations nodes |> ignore)
                    "Semantic constants cannot bypass the schema's bound")
            testCase "an interaction default does not loosen schema-required input" (fun () ->
                let nodes = nodes ()

                let defaulted =
                    { annotation with
                        Default = Some(BindingValue.Text "default")
                    }

                let inputs, _ = Bindings.bind (Map.ofList [ memberKey, defaulted ]) nodes
                Expect.isFalse inputs.Head.Binding.Required "An interface can supply the default"

                match nodes.Head with
                | BindingNode.Input input ->
                    Expect.isTrue input.Required "The request member remains wire-required"
                | _ -> failtest "Expected the authored schema leaf")
        ]
