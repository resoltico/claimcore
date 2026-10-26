module ClaimCore.Tests.CodecDefinitionTests

open System
open Expecto
open ClaimCore.Json

let private definitions () =
    let text = Schema.String(None, None, None, None)
    let original = Map.ofList [ "Shared", text ]
    Expect.equal (SchemaDefinitions.merge original original) original "Identical meaning is shared"

    Expect.equal
        (SchemaDefinitions.merge original (Map.ofList [ "Other", Schema.Boolean ]))
        (Map.ofList [ "Shared", text; "Other", Schema.Boolean ])
        "Distinct names coexist"

    Expect.throwsT<ArgumentException>
        (fun () ->
            SchemaDefinitions.merge original (Map.ofList [ "Shared", Schema.Boolean ])
            |> ignore)
        "A repeated name cannot acquire another meaning"

    Expect.throwsT<ArgumentException>
        (fun () ->
            Codec.record (fun text flag -> text, flag)
            |> Codec.field "text" fst (Codec.text |> Codec.named "Shared")
            |> Codec.field "flag" snd (Codec.boolean |> Codec.named "Shared")
            |> Codec.build
            |> ignore)
        "Actual codec assembly uses the shared rejection"

let private bounds () =
    for minimum, maximum in [ -1, 1; 2, 1; 0, -1 ] do
        Expect.throwsT<ArgumentException>
            (fun () -> Codec.boundedText minimum maximum |> ignore)
            "Text lengths stay nonnegative and ordered"

        Expect.throwsT<ArgumentException>
            (fun () -> Codec.boundedList minimum maximum Codec.boolean |> ignore)
            "Array lengths stay nonnegative and ordered"

    let exact = 9007199254740991L

    for minimum, maximum in [ -exact - 1L, exact; -exact, exact + 1L; 1L, 0L ] do
        Expect.throwsT<ArgumentException>
            (fun () -> Codec.integer minimum maximum |> ignore)
            "Integer ranges stay ordered within exact JSON numbers"

    let integers = Codec.integer (-exact) exact
    Expect.isOk (integers.Read "$" (JsonValue.Integer(-exact))) "Signed lower limit remains valid"
    Expect.isOk (integers.Read "$" (JsonValue.Integer exact)) "Upper limit remains valid"
    Expect.isOk ((Codec.boundedText 0 0).Read "$" (JsonValue.String "")) "Empty text range"

    Expect.isOk
        ((Codec.boundedList 0 0 Codec.boolean).Read "$" (JsonValue.Array []))
        "Empty array range"

[<Tests>]
let tests =
    testList
        "codec definition invariants"
        [
            testCase
                "shared definitions reject conflicts in merge and codec construction"
                definitions
            testCase "all range constructors retain ordering and representability checks" bounds
        ]
