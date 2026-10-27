module ClaimCore.Tests.CodecDefinitionTests

open System
open Expecto
open ClaimCore.Json

[<RequireQualifiedAccess>]
type private Sample =
    | Empty
    | Other
    | Number of int64

type private Wrapped = Wrapped of string * Sample

let private alternatives () =
    [
        Codec.unitVariant "EMPTY" Sample.Empty
        Codec.variant
            "NUMBER"
            Sample.Number
            (function
            | Sample.Number value -> Some value
            | _ -> None)
            (Codec.record id |> Codec.field "count" id (Codec.integer 0L 10L))
        Codec.unitVariant "OTHER" Sample.Other
    ]

let private discriminator () =
    let codec = Codec.taggedOn "code" "CodeEnvelope" (alternatives ())

    let expected =
        JsonValue.Object [ "code", JsonValue.String "NUMBER"; "count", JsonValue.Integer 7L ]

    Expect.equal
        (codec.Write(Sample.Number 7L))
        expected
        "The configured discriminator owns the envelope"

    Expect.equal
        (codec.Read "$" expected)
        (Ok(Sample.Number 7L))
        "The same declaration reads the fields"

    Expect.isError
        (codec.Read
            "$"
            (JsonValue.Object [ "kind", JsonValue.String "NUMBER"; "count", JsonValue.Integer 7L ]))
        "The default discriminator is not a compatibility alias"

    Expect.equal
        (codec.Definitions["CodeEnvelope"])
        (Schema.OneOf(
            [
                Schema.Ref "CodeEnvelope.EMPTY"
                Schema.Ref "CodeEnvelope.NUMBER"
                Schema.Ref "CodeEnvelope.OTHER"
            ],
            Some(
                "code",
                [
                    "EMPTY", "CodeEnvelope.EMPTY"
                    "NUMBER", "CodeEnvelope.NUMBER"
                    "OTHER", "CodeEnvelope.OTHER"
                ]
            )
        ))
        "The schema uses the same discriminator and case identities"

    Expect.equal
        ((Codec.tagged "KindEnvelope" (alternatives ())).Write(Sample.Number 7L))
        (JsonValue.Object [ "kind", JsonValue.String "NUMBER"; "count", JsonValue.Integer 7L ])
        "Existing tagged codecs retain their kind envelope"

let private groupedSchemas () =
    let codec = Codec.groupedTaggedOn "code" "Compact" (alternatives ())

    Expect.equal
        (codec.Definitions["Compact.EMPTY"])
        (Schema.Object(
            [ "code", Schema.OneOf([ Schema.Const "EMPTY"; Schema.Const "OTHER" ], None) ],
            []
        ))
        "Only equal payload schemas share a tagged shape"

    for value, json in
        [
            Sample.Empty, JsonValue.Object [ "code", JsonValue.String "EMPTY" ]
            Sample.Other, JsonValue.Object [ "code", JsonValue.String "OTHER" ]
            Sample.Number 7L,
            JsonValue.Object [ "code", JsonValue.String "NUMBER"; "count", JsonValue.Integer 7L ]
        ] do
        Expect.equal (codec.Write value) json "Grouping retains literal wire fields"
        Expect.equal (codec.Read "$" json) (Ok value) "Grouping retains each typed constructor"

    Expect.isError
        (codec.Read
            "$"
            (JsonValue.Object [ "code", JsonValue.String "OTHER"; "count", JsonValue.Integer 7L ]))
        "An empty payload cannot acquire a different case's fields"

let private singletonSchema () =
    Expect.equal
        (SchemaJson.value "#/$defs/" (Schema.OneOf([ Schema.Const "ONLY" ], None)))
        (JsonValue.Object [ "const", JsonValue.String "ONLY" ])
        "A one-alternative choice renders its exact constraint directly"

let private flattening () =
    let extra = Codec.record id |> Codec.field "target" id Codec.text

    let cases =
        alternatives ()
        |> List.map (
            Codec.extendVariant
                (fun target value -> Wrapped(target, value))
                (fun (Wrapped(target, value)) -> Some(target, value))
                extra
        )

    let codec = Codec.taggedOn "code" "Wrapped" cases

    let expected =
        JsonValue.Object
            [
                "code", JsonValue.String "NUMBER"
                "target", JsonValue.String "field"
                "count", JsonValue.Integer 7L
            ]

    Expect.equal
        (codec.Write(Wrapped("field", Sample.Number 7L)))
        expected
        "Additional typed fields stay flat"

    Expect.equal
        (codec.Read "$" expected)
        (Ok(Wrapped("field", Sample.Number 7L)))
        "Both typed payloads survive admission"

    Expect.isError
        (codec.Read
            "$"
            (JsonValue.Object
                [
                    "code", JsonValue.String "NUMBER"
                    "target", JsonValue.String "field"
                    "count", JsonValue.Integer 7L
                    "kind", JsonValue.String "NUMBER"
                ]))
        "The combined boundary rejects foreign members"

let private taggedCollisions () =
    Expect.throwsT<ArgumentException>
        (fun () ->
            Codec.taggedOn
                "code"
                "Duplicate"
                [
                    Codec.unitVariant "SAME" Sample.Empty
                    Codec.unitVariant "SAME" (Sample.Number 1L)
                ]
            |> ignore)
        "A code cannot select two alternatives"

    Expect.throwsT<ArgumentException>
        (fun () ->
            [
                Codec.variant
                    "NUMBER"
                    Sample.Number
                    (function
                    | Sample.Number value -> Some value
                    | _ -> None)
                    (Codec.record id |> Codec.field "code" id (Codec.integer 0L 10L))
            ]
            |> Codec.taggedOn "code" "Reserved"
            |> ignore)
        "A payload cannot redeclare its discriminator"

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
                "equal tagged payload schemas share shapes without widening cases"
                groupedSchemas
            testCase "single-alternative schemas retain their exact constraint" singletonSchema
            testCase
                "tagged envelopes share their configured discriminator with the schema"
                discriminator
            testCase
                "extended alternatives preserve typed fields and one closed boundary"
                flattening
            testCase
                "tagged declarations reject ambiguous codes and reserved fields"
                taggedCollisions
            testCase
                "shared definitions reject conflicts in merge and codec construction"
                definitions
            testCase "all range constructors retain ordering and representability checks" bounds
        ]
