module ClaimCore.Tests.RepresentationNegotiationTests

open System.Text
open Expecto
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Json

[<Tests>]
let tests =
    testList
        "transport representation negotiation"
        [
            testCase
                "missing selects DATA and header values normalize without unknown fallback"
                (fun () ->
                    for header, expected in
                        [
                            None, Ok Representation.Data
                            Some [ "human" ], Ok Representation.Human
                            Some [ "dAtA" ], Ok Representation.Data
                            Some [ " HUMAN\t"; "human" ], Ok Representation.Human
                            Some [ "DATA, data" ], Ok Representation.Data
                            Some [], Error()
                            Some [ "" ], Error()
                            Some [ "SCREEN" ], Error()
                            Some [ "HUMAN"; "DATA" ], Error()
                            Some [ "HUMAN,DATA" ], Error()
                            Some [ "DATA," ], Error()
                        ] do
                        Expect.equal
                            (Representation.select header)
                            expected
                            "Explicit header contract")
            testCase
                "reply branches are closed discriminated structures with correlated selection"
                (fun () ->
                    let human =
                        Codec.record id
                        |> Codec.field "document" id PresentationCodecs.view
                        |> Codec.build

                    let data =
                        Codec.record id
                        |> Codec.field "count" id (Codec.integer 0L 10L)
                        |> Codec.build

                    let codec = RepresentationCodecs.reply "AuthoredRepresentation" human data

                    for raw in
                        [
                            """{"document":{"regions":[]}}"""
                            """{"representation":"human","document":{"regions":[]}}"""
                            """{"representation":"SCREEN","document":{"regions":[]}}"""
                            """{"representation":"HUMAN","count":1}"""
                            """{"representation":"DATA","document":{"regions":[]}}"""
                            """{"representation":"HUMAN","document":{"regions":[]},"count":1}"""
                        ] do
                        Expect.isError
                            (Codec.validate 8192 codec (Encoding.UTF8.GetBytes raw))
                            "No missing discriminator, fallback or mixed response"

                    let decoded =
                        Codec.decode
                            8192
                            codec
                            (Encoding.UTF8.GetBytes
                                """{"representation":"HUMAN","document":{"regions":[]}}""")
                        |> Result.defaultWith (fun error -> failtest error.Reason)

                    Expect.isTrue
                        (RepresentationReply.matches Representation.Human decoded)
                        "Requested HUMAN correlates"

                    Expect.isFalse
                        (RepresentationReply.matches Representation.Data decoded)
                        "A DATA requester must reject HUMAN"

                    Expect.isOk
                        (Codec.decode
                            8192
                            codec
                            (Encoding.UTF8.GetBytes """{"representation":"DATA","count":1}"""))
                        "Independent DATA control")
        ]
