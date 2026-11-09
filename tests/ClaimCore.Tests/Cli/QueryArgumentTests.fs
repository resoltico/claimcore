module ClaimCore.Tests.QueryArgumentTests

open System
open Expecto
open ClaimCore.Cli
open ClaimCore.Wire
open ClaimCore.Domain
open ClaimCore.Application.Contracts

let private prepare arguments =
    Grammar.prepare (fun () -> failtest "A read never allocates an operation identity") arguments

let private invocation arguments =
    prepare arguments
    |> Result.defaultWith (fun errors -> failtestf "%A" errors)
    |> _.Invocation

[<Tests>]
let tests =
    testList
        "typed query argv assembly"
        [
            testCase
                "read operands and paging produce their declared endpoints without allocating operation IDs"
                (fun () ->
                    for args, endpoint in
                        [
                            [ "case"; "get"; "QUERY" ], Api.caseGet.Id
                            [ "note"; "list"; "QUERY" ], Api.caseNotes.Id
                            [ "payment"; "list"; "QUERY" ], Api.casePayments.Id
                            [ "case"; "timeline"; "QUERY" ], Api.caseTimeline.Id
                            [ "authority"; "grants"; "00000000-0000-4000-8000-000000000001" ],
                            Api.authorityGrants.Id
                            [ "operation"; "get"; "019aa6a0-0000-7000-8000-000000000001" ],
                            Api.operationGet.Id
                            [ "context" ], Api.context.Id
                        ] do
                        Expect.equal
                            (Frames.endpoint (invocation args)).Id
                            endpoint
                            "Typed endpoint retained"

                    Expect.isError (prepare [ "operation"; "get" ]) "Lookup cannot invent an ID")
            testCase "note and payment defaults remain distinct from a directory page" (fun () ->
                let notes =
                    Frames.value Api.caseNotes (invocation [ "note"; "list"; "QUERY" ])
                    |> Option.get

                let payments =
                    Frames.value Api.casePayments (invocation [ "payment"; "list"; "QUERY" ])
                    |> Option.get

                let actors =
                    Frames.value Api.authorityList (invocation [ "authority"; "list" ])
                    |> Option.get

                Expect.equal notes.Limit 20 "Bounded note page"
                Expect.equal payments.Limit 20 "Payment navigation default"
                Expect.equal actors.Limit 50 "Directory default"

                Expect.isError
                    (prepare [ "note"; "list"; "QUERY"; "--limit"; "21" ])
                    "Note page maximum is enforced at acquisition"

                Expect.isError
                    (prepare [ "payment"; "list"; "QUERY"; "--limit"; "51" ])
                    "Payment page maximum")
            testCase "read filters retain separate vocabularies and explicit false" (fun () ->
                let frame =
                    invocation
                        [
                            "reminder"
                            "list"
                            "--view"
                            "completed"
                            "--include-inactive"
                            "false"
                            "--case-reference"
                            "00000000-0000-4000-8000-000000000001"
                        ]

                let value = Frames.value Api.reminders frame |> Option.get
                Expect.equal value.View (Some ReminderView.Completed) "Core reminder choice"
                Expect.equal value.IncludeInactive (Some false) "False is not omission"

                Expect.equal
                    value.Target
                    (Some(CaseSelector.Reference "00000000-0000-4000-8000-000000000001"))
                    "UUID-shaped reference is still a reference"

                Expect.isError
                    (prepare [ "reminder"; "list"; "--state"; "voided" ])
                    "Case lifecycle is not a reminder state")
            testCase
                "invalid cursor and numeric bounds are usage failures before transport encoding"
                (fun () ->
                    for args in
                        [
                            [ "case"; "list"; "--limit"; "0" ]
                            [ "case"; "list"; "--limit"; "9007199254740992" ]
                            [ "note"; "list"; "QUERY"; "--cursor"; "" ]
                            [ "authority"; "list"; "--cursor"; String.replicate 513 "x" ]
                        ] do
                        Expect.isError (prepare args) "No malformed typed query can reach delivery")
        ]
