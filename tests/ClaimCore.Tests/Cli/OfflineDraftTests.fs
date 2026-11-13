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
            testCase
                "all mutation subjects omit operation IDs while retaining authored intent"
                (fun () ->
                    for args in
                        [
                            [
                                "people"
                                "set-profile"
                                "--principal-kind"
                                "human"
                                "--issuer"
                                "https://issuer.example/"
                                "--subject"
                                "offline-person"
                                "--name"
                                "Editable profile"
                            ]
                            [ "note"; "add"; "OFFLINE"; "--text"; "Authored note" ]
                            [
                                "reminder"
                                "create"
                                "OFFLINE"
                                "--action"
                                "Authored action"
                                "--due"
                                "2026-11-10"
                            ]
                            [ "case"; "close"; "OFFLINE"; "--expected-revision"; "7" ]
                            [
                                "work"
                                "update"
                                "OFFLINE"
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
                            "OFFLINE"
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

                    match input with
                    | ClaimCore.Application.Contracts.OperationInput.Case command ->
                        Expect.equal
                            command.OperationId
                            (System.Guid.Parse "10000000-0000-7000-8000-000000000072")
                            "Supplied identity"

                        Expect.equal command.ExpectedRevision 7L "Supplied reviewed revision"
                    | _ -> failtest "Case intent required")
        ]
