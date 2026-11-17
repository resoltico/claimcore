module ClaimCore.Tests.CliOwnedHelpTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Collections.Generic
open Expecto
open ClaimCore.Cli
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Application.Contracts

let private required =
    function
    | Ok value -> value
    | Error error -> failtestf "Expected valid CLI input: %A" error

let private verb arguments =
    match CliInvocations.admit arguments |> required with
    | CliInvocation.Verb values ->
        Grammar.prepare (fun () -> failtest "Queries never allocate") values |> required
    | value -> failtestf "A consumed help spelling is data, not help: %A" value

let private helpSynopsis (json: JsonDocument) =
    let operation = json.RootElement.GetProperty("operations")[0]
    operation.GetProperty("synopsis").GetString() |> nonNull

[<Tests>]
let grammar =
    testList
        "CLI owned help grammar"
        [
            testCase
                "optional named case ID is discoverable without duplicating the positional alternative"
                (fun () ->
                    for draft in [ false; true ] do
                        let synopsis = CommandGrammar.synopsisForDraft draft "reminder.list"

                        Expect.stringContains
                            synopsis
                            "[--case-id "
                            "The accepted optional ID selector appears in actual syntax"

                        Expect.stringContains
                            synopsis
                            "[--case-reference VALUE]"
                            "The reference remains an optional named selector"

                        Expect.isFalse
                            (synopsis.Contains "[--] CASE-REFERENCE")
                            "The optional selector is not invented as an operand"

                    let help =
                        Discovery.commandHelp
                            (RenderingContext.forLanguage "en")
                            [ "reminder"; "list" ]
                        |> Option.get

                    Expect.stringContains
                        help
                        "[--case-id "
                        "Rendered command help includes the actual argument"

                    use json =
                        JsonDocument.Parse(
                            Discovery.commandHelpJson
                                (RenderingContext.forLanguage "en")
                                [ "reminder"; "list" ]
                            |> Option.get
                        )

                    Expect.stringContains
                        (helpSynopsis json)
                        "[--case-id "
                        "Machine help syntax agrees with executable syntax"

                    let schema =
                        Discovery.schema (CliSchema.Request "reminder.list")
                        |> Option.get
                        |> Encoding.UTF8.GetString

                    Expect.stringContains
                        schema
                        "\"caseId\""
                        "The discovered request schema retains ID selection"

                    let id = "10000000-0000-4000-8000-000000000001"
                    let prepared = verb [ "reminder"; "list"; "--case-id"; id ]
                    let input = Frames.value Api.reminders prepared.Invocation |> Option.get

                    Expect.equal
                        input.Target
                        (Some(CaseSelector.Id(CaseId.create (Guid.Parse id) |> Option.get)))
                        "The displayed named selector is actually admitted"

                    let positional = CommandGrammar.synopsis "case.get"

                    Expect.equal
                        (positional.Split("--case-id", StringSplitOptions.None).Length - 1)
                        1
                        "A true positional alternative is still displayed once")
            testCase "help aliases consumed by any named option remain values" (fun () ->
                for arguments, expected in
                    [
                        [ "people"; "list"; "--text"; "-h" ], "-h"
                        [ "people"; "list"; "--text=-h" ], "-h"
                        [ "people"; "list"; "--text=--help" ], "--help"
                    ] do
                    let input =
                        (verb arguments).Invocation |> Frames.value Api.peopleList |> Option.get

                    Expect.equal
                        input.Text
                        (Some expected)
                        "Text acquired by the grammar is unchanged"

                match CliInvocations.admit [ "note"; "add"; "--text-file"; "-h" ] |> required with
                | CliInvocation.Verb arguments ->
                    Expect.equal
                        (List.last arguments)
                        "-h"
                        "A source file path is acquired by the same lexical value rule"
                | other -> failtestf "A file path cannot become a help request: %A" other

                let page =
                    (verb [ "people"; "requests"; "--cursor"; "-h" ]).Invocation
                    |> Frames.value Api.peopleRequests
                    |> Option.get

                Expect.equal page.AfterCursor (Some "-h") "The rule is not specific to --text"

                let principal =
                    (verb
                        [
                            "people"
                            "requests"
                            "dismiss"
                            "--principal-kind=HUMAN"
                            "--issuer=https://issuer.example/"
                            "--subject"
                            "-h"
                        ])
                        .Invocation
                    |> Frames.value Api.peopleRequestsDismiss
                    |> Option.get

                Expect.equal
                    principal.Subject
                    "-h"
                    "Operational named inputs use the same token consumption")
            testCase
                "standalone aliases and sentinel operands retain their owned meanings"
                (fun () ->
                    for alias in [ "-h"; "--help" ] do
                        match CliInvocations.admit [ "people"; "list"; alias ] |> required with
                        | CliInvocation.CommandHelp(words, _, _) ->
                            Expect.equal
                                words
                                [ "people"; "list" ]
                                "A standalone alias requests this command's help"
                        | other -> failtestf "Expected command help: %A" other

                        let input =
                            (verb [ "case"; "get"; "--"; alias ]).Invocation
                            |> Frames.value Api.caseGet
                            |> Option.get

                        Expect.equal
                            input.Target
                            (CaseSelector.Reference alias)
                            "The sentinel protects even a help alias")
        ]

[<Tests>]
let program =
    testSequenced (
        testList
            "CLI owned help executable"
            [
                testCase
                    "outer Program returns literal named values and current reminder help syntax"
                    (fun () ->
                        let directory = Directory.CreateTempSubdirectory "claimcore-owned-help-"

                        try
                            let environment = Dictionary<string, string>()
                            environment["XDG_CONFIG_HOME"] <- directory.FullName
                            environment["CLAIMCORE_LANGUAGE"] <- "en"
                            environment["CLAIMCORE_DISPLAY_LOCALE"] <- "en-GB"

                            for tail, expected in
                                [
                                    [ "--text"; "-h" ], "-h"
                                    [ "--text=-h" ], "-h"
                                    [ "--text=--help" ], "--help"
                                ] do
                                let result =
                                    CliOffline.invoke
                                        ([ "people"; "list"; "--dry-run" ] @ tail)
                                        environment
                                        None

                                Expect.equal
                                    result.ExitCode
                                    0
                                    "The real executable dispatcher does not steal acquired text"

                                Expect.equal
                                    result.StandardError.Length
                                    0
                                    "No spurious usage error"

                                use json = JsonDocument.Parse result.StandardOutput

                                Expect.equal
                                    (json.RootElement.GetProperty("endpoint").GetString())
                                    "people.list"
                                    "The executable retains the query task"

                                Expect.equal
                                    (json.RootElement
                                        .GetProperty("input")
                                        .GetProperty("text")
                                        .GetString())
                                    expected
                                    "Actual output preserves the acquired scalar"

                            let help =
                                CliOffline.invoke
                                    [ "help"; "reminder"; "list"; "--format=json" ]
                                    environment
                                    None

                            Expect.equal help.ExitCode 0 "Actual JSON help executes offline"
                            use json = JsonDocument.Parse help.StandardOutput

                            Expect.stringContains
                                (helpSynopsis json)
                                "[--case-id "
                                "Executable help names the supported selector"
                        finally
                            directory.Delete(true))
            ]
    )
