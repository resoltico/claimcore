module ClaimCore.Tests.OfflineDraftTests

open System.Text
open System.Text.Json
open Expecto
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Wire

let private draft arguments =
    Grammar.prepareOffline arguments
    |> function
        | Ok(Some value) -> value
        | other -> failtestf "Expected offline draft: %A" other

[<Tests>]
let tests =
    testList
        "offline drafts"
        [
            testCase
                "case work and reminder metadata remain omitted without network or fabricated values"
                (fun () ->
                    for arguments, revisionPath in
                        [
                            [ "case"; "close"; "OFFLINE"; "--dry-run" ], "expectedRevision"
                            [
                                "work"
                                "update"
                                "OFFLINE"
                                "--handling-summary"
                                "Current summary"
                                "--dry-run"
                            ],
                            "expectedRevision"
                            [
                                "reminder"
                                "complete"
                                "OFFLINE"
                                "--reminder-id"
                                "10000000-0000-7000-8000-000000000071"
                                "--dry-run"
                            ],
                            "act.expectedRevision"
                        ] do
                        let value = draft arguments
                        Expect.isNone value.Issues "Authored syntax is valid"
                        use json = JsonDocument.Parse(value.Bytes)
                        let input = json.RootElement.GetProperty("input")
                        let mutable ignored = Unchecked.defaultof<JsonElement>

                        Expect.isFalse
                            (input.TryGetProperty("operationId", &ignored))
                            "No operation is allocated"

                        let parent, key =
                            if revisionPath.StartsWith("act.") then
                                input.GetProperty("act"), "expectedRevision"
                            else
                                input, revisionPath

                        Expect.isFalse
                            (parent.TryGetProperty(key, &ignored))
                            "A draft is not an executable operation"

                        Expect.isError
                            (Frames.admit value.Bytes)
                            "Delivery rejects incomplete metadata")
            testCase "other authored syntax still uses the Domain owner" (fun () ->
                let value =
                    draft [ "work"; "update"; "OFFLINE"; "--handling-summary"; "\n"; "--dry-run" ]

                Expect.isSome value.Issues "Invalid narrative is not excused by a missing version"
                let fields = value.Issues |> Option.get |> InputIssues.toList |> List.map _.Target

                Expect.contains
                    fields
                    (InputTarget.Field BusinessField.HandlingSummary)
                    "The actual field is reported")
            testCase "automation still requires its own reviewed version" (fun () ->
                match
                    Grammar.prepare
                        (fun () -> "10000000-0000-7000-8000-000000000072")
                        [ "case"; "close"; "OFFLINE" ]
                with
                | Error issues ->
                    Expect.contains
                        issues
                        (ClaimCore.Presentation.UsageError.MissingOption "--expected-revision")
                        "Automation has no implicit read"
                | Ok _ -> failtest "An executable command cannot invent its version")
            testCase "complete metadata keeps the established executable dry-run shape" (fun () ->
                let args =
                    [
                        "case"
                        "close"
                        "OFFLINE"
                        "--expected-revision"
                        "7"
                        "--operation-id"
                        "10000000-0000-7000-8000-000000000072"
                        "--dry-run"
                    ]

                Expect.equal
                    (Grammar.prepareOffline args |> Result.map Option.isNone)
                    (Ok true)
                    "Only omitted versions use draft admission"

                let prepared =
                    Grammar.prepare (fun () -> failtest "No allocation") args
                    |> function
                        | Ok value -> value
                        | Error issues -> failtestf "%A" issues

                Expect.isOk (Frames.admit prepared.Bytes) "Explicit metadata is an admitted frame")
        ]
