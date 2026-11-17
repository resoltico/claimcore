module ClaimCore.Tests.ExportTransportTests

open System.Text
open Expecto
open ClaimCore.Json
open ClaimCore.Wire

let private admit (value: string) =
    Codec.decode Limits.MaximumRequestBytes ExportTransport.request (Encoding.UTF8.GetBytes value)

[<Tests>]
let tests =
    testList
        "CSV export media contract"
        [
            testCase
                "exactly four query selections are closed and never accept traversal cursors"
                (fun () ->
                    for value in
                        [
                            """{"query":"case.list","selection":{"view":"MINE"}}"""
                            """{"query":"case.timeline","selection":{"target":{"kind":"REFERENCE","caseReference":"A"},"filter":"FACTS"}}"""
                            """{"query":"reminder.list","selection":{"assignment":"MINE","state":"PENDING","due":"TODAY","target":{"kind":"REFERENCE","caseReference":"A"}}}"""
                            """{"query":"people.history","selection":{"actorId":"10000000-0000-4000-8000-000000000001"}}"""
                        ] do
                        Expect.isOk (admit value) "Existing query selection"

                    for value in
                        [
                            """{"query":"case.payments","selection":{}}"""
                            """{"query":"case.list","selection":{"cursor":"old"}}"""
                            """{"query":"case.list","selection":{"limit":1}}"""
                            """{"query":"reminder.list","selection":{"state":"INVALID"}}"""
                            """{"query":"people.history","selection":{},"representation":"CSV"}"""
                        ] do
                        Expect.isError
                            (admit value)
                            "No alias, cursor, limit or third representation")
            testCase
                "the published path declares CSV success and JSON failures and enters the identity document"
                (fun () ->
                    let document =
                        OpenApi.document |> WireWriter.indentedBytes |> Encoding.UTF8.GetString

                    Expect.stringContains document "\"/api/export\"" "Published route"
                    Expect.stringContains document "\"text/csv\"" "CSV success media"

                    Expect.stringContains
                        document
                        "\"ExportFailureReply\""
                        "Typed JSON failure root"

                    Expect.stringContains document "\"ExportRequest\"" "Closed request root"
                    Expect.stringContains document "\"Content-Length\"" "Complete length metadata"

                    Expect.isTrue
                        (Map.containsKey "ExportFailureReply" OpenApi.definitions)
                        "Generated admission root is reachable")
        ]
