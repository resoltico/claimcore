module ClaimCore.Tests.GoldenFilesTests

open Expecto
open ClaimCore.Json
open ClaimCore.Protocol
open ClaimCore.TestSupport

[<Tests>]
let tests =
    testList
        "declared golden admission"
        [
            testCase "CI refuses updates even for a declared golden" (fun () ->
                Expect.isError
                    (GoldenFiles.admit
                        (Set.singleton "wire-request-examples.json")
                        "wire-request-examples.json"
                        true
                        true)
                    "CI presence refuses update mode")
            testCase "protected vectors cannot be unlocked by inventory declarations" (fun () ->
                for filename in
                    [
                        "record-format-vectors.json"
                        "transition-matrix.tsv"
                        "client-byte-conformance.json"
                    ] do
                    Expect.isError
                        (GoldenFiles.admit (Set.singleton filename) filename true false)
                        "Permanent bytes remain independent")
            testCase "undeclared files and directory traversal refuse" (fun () ->
                for filename in
                    [ "other.json"; "../inventory.json"; "nested/file.json"; "nested\\file.json" ] do
                    let declared =
                        if filename = "other.json" then
                            Set.empty
                        else
                            Set.singleton filename

                    Expect.isError
                        (GoldenFiles.admit declared filename true false)
                        "Only declared fixture basenames")
            testCase "ordinary checks never write and an admitted update is explicit" (fun () ->
                let files = Set.singleton "wire-request-examples.json"

                Expect.equal
                    (GoldenFiles.admit files "wire-request-examples.json" false true)
                    (Ok false)
                    "Checks stay read-only"

                Expect.equal
                    (GoldenFiles.admit files "wire-request-examples.json" true false)
                    (Ok true)
                    "Local explicit update")
            testCase "positive overrides retain authored values and undeclared members" (fun () ->
                let input =
                    JsonValue.Object
                        [
                            "registration",
                            JsonValue.Object
                                [
                                    "claimantName", JsonValue.String "Authored literal"
                                    "undeclared", JsonValue.String "still present"
                                ]
                        ]

                let completed = Inputs.positiveValue input

                match JsonValue.tryMember "registration" completed with
                | Some(JsonValue.Object members) ->
                    Expect.equal
                        (List.tryFind (fst >> (=) "claimantName") members)
                        (Some("claimantName", JsonValue.String "Authored literal"))
                        "Literal override"

                    Expect.equal
                        (List.tryFind (fst >> (=) "undeclared") members)
                        (Some("undeclared", JsonValue.String "still present"))
                        "Unknown input is not silently dropped"

                    Expect.isOk
                        (RequestCodecs.registration.Read
                            "$"
                            (JsonValue.Object(members |> List.filter (fst >> (<>) "undeclared"))))
                        "Removing only the unknown member admits the positive builder input"

                    Expect.isError
                        (RequestCodecs.registration.Read "$" (JsonValue.Object members))
                        "The actual codec still rejects unknown members"
                | _ -> failtest "Expected a group object")
        ]

[<Tests>]
let cliArguments =
    testCase "omitted CLI business inputs remain reusable captured arguments" (fun () ->
        let arguments = CliExamples.complete [ "case"; "open"; "SYNTHETIC" ]

        let captured =
            System.String
                .Join(" ", arguments)
                .Split(' ', System.StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList

        let prepare values =
            ClaimCore.Cli.Grammar.prepare (fun () -> "11111111-2222-4333-8444-555555555555") values
            |> Result.map _.Bytes

        Expect.equal
            (prepare captured)
            (prepare arguments)
            "The golden argument column can be read back by its consumers")
