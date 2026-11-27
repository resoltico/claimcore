module ClaimCore.Tests.OfflineDraftTests

open ClaimCore.CommandLine

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
                            [
                                "case"
                                "close"
                                "--outcome"
                                "other"
                                "CC-BGYE-HN4P-W9H6-QPV6-64"
                                "--dry-run"
                            ],
                            "expectedRevision"
                            [
                                "work"
                                "update"
                                "CC-BGYE-HN4P-W9H6-QPV6-64"
                                "--handling-summary"
                                "Current summary"
                                "--dry-run"
                            ],
                            "expectedRevision"
                            [
                                "reminder"
                                "complete"
                                "CC-BGYE-HN4P-W9H6-QPV6-64"
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

                        let parent, key = input.GetProperty("binding"), "expectedRevision"
                        ignore revisionPath

                        Expect.isFalse
                            (parent.TryGetProperty(key, &ignored))
                            "A draft is not an executable operation"

                        Expect.isError
                            (Frames.admit value.Bytes)
                            "Delivery rejects incomplete metadata")
            testCase "other authored syntax still uses the Domain owner" (fun () ->
                let value =
                    draft
                        [
                            "work"
                            "update"
                            "CC-BGYE-HN4P-W9H6-QPV6-64"
                            "--handling-summary"
                            ""
                            "--dry-run"
                        ]

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
                        [ "case"; "close"; "--outcome"; "other"; "CC-BGYE-HN4P-W9H6-QPV6-64" ]
                with
                | Error issues ->
                    Expect.contains
                        issues
                        (ClaimCore.CommandLine.UsageError.MissingOption "--expected-revision")
                        "Automation has no implicit read"
                | Ok _ -> failtest "An executable command cannot invent its version")
            testCase
                "all mutation subjects omit operation IDs while retaining authored intent"
                (fun () ->
                    for args in
                        [
                            [
                                "people"
                                "set-profile"
                                "10000000-0000-7000-8000-000000000001"
                                "--name"
                                "Editable profile"
                            ]
                            [
                                "note"
                                "add"
                                "CC-BGYE-HN4P-W9H6-QPV6-64"
                                "--text"
                                "Authored note"
                            ]
                            [
                                "reminder"
                                "create"
                                "CC-BGYE-HN4P-W9H6-QPV6-64"
                                "--action"
                                "Authored action"
                                "--due"
                                "2026-11-10"
                            ]
                            [
                                "case"
                                "close"
                                "--outcome"
                                "other"
                                "CC-BGYE-HN4P-W9H6-QPV6-64"
                                "--expected-revision"
                                "7"
                            ]
                            [
                                "work"
                                "update"
                                "CC-BGYE-HN4P-W9H6-QPV6-64"
                                "--expected-work-revision"
                                "0"
                                "--handling-summary"
                                "Authored handover"
                            ]
                        ] do
                        let value = draft (args @ [ "--dry-run" ])
                        Expect.isNone value.Issues "Only available authored syntax is admitted"
                        use json = JsonDocument.Parse(value.Bytes)

                        Expect.isFalse
                            (json.RootElement.GetProperty("input").TryGetProperty("operationId")
                             |> fst)
                            "No allocated or dummy UUID"

                        Expect.isError
                            (Grammar.prepare
                                (fun () -> failtest "Dry-run cannot allocate an identity")
                                (args @ [ "--dry-run" ]))
                            "Incomplete drafts cannot enter executable preparation")
            testCase
                "complete explicit metadata keeps the exact command in the offline output"
                (fun () ->
                    let args =
                        [
                            "case"
                            "close"
                            "--outcome"
                            "other"
                            "CC-BGYE-HN4P-W9H6-QPV6-64"
                            "--expected-revision"
                            "7"
                            "--operation-id"
                            "10000000-0000-7000-8000-000000000072"
                            "--dry-run"
                        ]

                    let value = draft args
                    Expect.isNone value.Issues "Explicit metadata is preserved"

                    let frame =
                        Frames.admit value.Bytes
                        |> Result.defaultWith (fun error -> failtestf "%A" error)

                    let input = Frames.value Api.operationExecute frame |> Option.get

                    Expect.equal
                        input.OperationId
                        (System.Guid.Parse "10000000-0000-7000-8000-000000000072")
                        "Supplied identity"

                    match (ClaimCore.TestSupport.Submissions.command input).Binding with
                    | Binding.Case(_, revision) ->
                        Expect.equal (Revision.value revision) 7L "Supplied reviewed revision"
                    | _ -> failtest "Case binding required")

        ]
