module ClaimCore.Tests.ResponseDecoderTests

open System
open Expecto
open ClaimCore.Json
open ClaimCore.ContractGeneration

let private refuses reason action =
    try
        action ()
        failtest "Generation must refuse unsupported declarations"
    with :? ArgumentException as error ->
        Expect.stringContains error.Message reason "The intended generator boundary refuses"

let private stringShape pattern format =
    Schema.String(pattern, format, None, None, None)

[<Tests>]
let tests =
    testList
        "Browser response descriptor generation"
        [
            testCase "every published scalar syntax is admitted" (fun () ->
                Expect.isNonEmpty (ResponseDecoders.source ()) "All owning roots are generated")
            testCase "all finite constructors have an emitted descriptor" (fun () ->
                let item = Schema.Const "A"

                let shape =
                    Schema.Object(
                        [
                            "selected", Schema.Array(item, 0, 1)
                            "rejected", Schema.Array(Schema.Object([ "key", item ], []), 0, 1)
                        ],
                        []
                    )

                let cases =
                    [
                        Schema.JsonObject, """["jsonObject"]"""
                        Schema.Object(
                            [ "required", Schema.Boolean; "optional", Schema.Null ],
                            [ "optional" ]
                        ),
                        """["object",{"required":["boolean"],"optional":["null"]},["optional"]]"""
                        Schema.Array(Schema.Integer(-1L, 1L), 0, 2),
                        """["array",["integer",-1,1],0,2]"""
                        Schema.String(None, Some "date", Some 1, Some 2, Some 4),
                        """["string",null,"date",1,2,4]"""
                        Schema.Boolean, """["boolean"]"""
                        Schema.Null, """["null"]"""
                        item, """["const","A"]"""
                        Schema.OneOf([ item; Schema.Const "B" ], None),
                        """["oneOf",[["const","A"],["const","B"]]]"""
                        Schema.Ref "Uuid", """["ref","Uuid"]"""
                        Schema.Vocabulary("authored", item), """["const","A"]"""
                        Schema.EnumPartition(shape, [ "A" ], "selected", "rejected", "key"),
                        """["partition",["object",{"selected":["array",["const","A"],0,1],"rejected":["array",["object",{"key":["const","A"]},[]],0,1]},[]],["A"],"selected","rejected","key"]"""
                    ]

                for shape, expected in cases do
                    Expect.equal
                        (ResponseDecoders.schemaDescriptor shape)
                        expected
                        "The constructor retains its constraints")
            testCase "unsupported formats refuse generation even inside compound schemas" (fun () ->
                for format in [ "email"; "URI"; "" ] do
                    let shape = Schema.Array(stringShape None (Some format), 0, 1)

                    refuses "Unsupported browser scalar format:" (fun () ->
                        ResponseDecoders.schemaDescriptor shape |> ignore))
            testCase
                "unsupported or malformed regex forms refuse before browser compilation"
                (fun () ->
                    for pattern in
                        [
                            "(?<name>a)"
                            "(?<=a)b"
                            "(?<!a)b"
                            "(?(1)a|b)"
                            "(?i:a)"
                            "(?>a)"
                            "a*?"
                            "(a)\\1"
                            "\\k<name>"
                            "\\Aabc\\z"
                            "\\d+"
                            "\\w+"
                            "\\bword\\b"
                            ".*"
                            "[a-z-[aeiou]]"
                            "[\\p{Letter}]"
                            "[\\P{Cc}]"
                            "[\\u0041]"
                            "\\-"
                            "[z-a]"
                            "a{2,1}"
                            "["
                            "("
                            "a)"
                            "\\"
                            "é"
                        ] do
                        refuses "Unsupported browser scalar pattern." (fun () ->
                            ResponseDecoders.schemaDescriptor (stringShape (Some pattern) None)
                            |> ignore))
            testCase "missing schema references refuse generation" (fun () ->
                refuses "Missing browser schema reference:" (fun () ->
                    ResponseDecoders.schemaDescriptor (Schema.Ref "NotAPublishedDefinition")
                    |> ignore))
        ]
