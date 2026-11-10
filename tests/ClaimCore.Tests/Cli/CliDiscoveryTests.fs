module ClaimCore.Tests.CliDiscoveryTests

open ClaimCore.Application.Contracts
open System
open System.IO
open System.Text
open Expecto
open ClaimCore.Cli
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Domain

let private identity () = "11111111-2222-7333-8444-555555555555"

let private inspect arguments =
    match CliInvocations.admit arguments with
    | Error issue -> 64, UsageText.renderHost "en" arguments issue
    | Ok(CliInvocation.Verb arguments) ->
        match Grammar.prepare identity arguments with
        | Error issues -> 64, UsageText.render "en" arguments issues
        | Ok _ -> 0, "prepared"
    | Ok invocation ->
        use output = new MemoryStream()
        use errors = new MemoryStream()
        let code = CliDiscovery.discovery output errors invocation

        code,
        Encoding.UTF8.GetString(output.ToArray())
        + Encoding.UTF8.GetString(errors.ToArray())

[<Tests>]
let tests =
    testList
        "CLI discovery usage matrix"
        [
            testCase "families list their actual verbs instead of empty unknown commands" (fun () ->
                for family in [ "case"; "work"; "reminder"; "authority"; "operation" ] do
                    for arguments in
                        [ [ family ]; [ "help"; family ]; [ family; "--help" ]; [ family; "-h" ] ] do
                        let code, text = inspect arguments
                        Expect.equal code 0 "A known family is offline discovery"

                        Expect.stringContains
                            text
                            (family + " ")
                            "The family's commands are listed"

                        Expect.isFalse
                            (text.Contains "Unknown command")
                            "A valid family is not unknown")
            testCase
                "every verb has help, contextual unknown options and visible extra tokens"
                (fun () ->
                    for operation in CommandGrammar.operations do
                        let words = CommandGrammar.words operation

                        for arguments in
                            [ words @ [ "--help" ]; words @ [ "-h" ]; [ "help" ] @ words ] do
                            let code, text = inspect arguments
                            Expect.equal code 0 "Every grammar node supports help"

                            Expect.stringContains
                                text
                                (String.concat " " words)
                                "Help identifies its command"

                            for flag in [ "--format"; "--language"; "--dry-run" ] do
                                Expect.stringContains text flag "Global options are discoverable"

                        let bareCode, bare = inspect words

                        Expect.contains
                            [ 0; 64 ]
                            bareCode
                            "Bare verbs prepare or report missing inputs"

                        Expect.isFalse
                            (bare.Contains "Unknown command")
                            "A known verb is not unknown"

                        let code, text = inspect (words @ [ "--unknown-option-witness" ])
                        Expect.equal code 64 "An unknown flag is a usage error"

                        Expect.stringContains
                            text
                            "--unknown-option-witness"
                            "The bad flag remains visible"

                        Expect.stringContains
                            text
                            (String.concat " " words)
                            "The bad flag names its command"

                        let inputs, _ = CommandGrammar.derive operation

                        let operands =
                            inputs
                            |> List.filter (fun input -> input.Argument = CommandArgument.Operand)
                            |> List.length

                        let extras =
                            List.init (operands + 1) (fun index -> "extra-token-" + string index)

                        let code, text = inspect (words @ extras)
                        Expect.equal code 64 "Excess operands are rejected"

                        Expect.stringContains
                            text
                            (List.last extras)
                            "The excess token remains visible")
            testCase "version aliases and unknown tokens are unambiguous and escaped" (fun () ->
                Expect.equal
                    (inspect [ "--version" ])
                    (inspect [ "version" ])
                    "The universal version alias is exact"

                Expect.isNone
                    (Discovery.commandHelp "en" [ "frobnicate" ])
                    "Invalid discovery nodes never return empty successful help"

                for suffix in [ [ "--help" ]; [ "--json" ] ] do
                    Expect.equal
                        (inspect ("--version" :: suffix))
                        (inspect ("version" :: suffix))
                        "Version aliases retain their supported options"

                for token in [ "frobnicate"; "-x"; "--"; "bell\u0007" ] do
                    let code, text = inspect [ token ]
                    Expect.equal code 64 "Unknown input is a usage error"

                    Expect.stringContains
                        text
                        (TerminalText.data token)
                        "Unknown data is terminal-escaped"

                    Expect.isFalse
                        (text.Contains "Unknown command: .")
                        "No empty interpolation remains")
            testCase
                "creation cannot clear absent facts and update can still clear optional text"
                (fun () ->
                    let operation kind =
                        CommandGrammar.operations
                        |> List.find (fun operation ->
                            operation.SelectedAct = Some(ActKind.Case kind))

                    Expect.isFalse
                        ((CommandGrammar.synopsis (operation CaseActKind.Open)).Contains "--clear-")
                        "OPEN offers no clears"

                    Expect.stringContains
                        (CommandGrammar.synopsis (operation CaseActKind.AmendRegistration))
                        "--clear-incident-description"
                        "Updates retain explicit clearing"

                    let code, text =
                        inspect
                            [
                                "case"
                                "open"
                                "WITNESS"
                                "--clear-incident-description"
                                "--dry-run"
                            ]

                    Expect.equal code 64 "Inapplicable clear refuses before preparation"

                    Expect.stringContains
                        text
                        "--clear-incident-description"
                        "The rejected option is named"

                    let _, getHelp = inspect [ "case"; "get"; "--help" ]

                    Expect.isFalse
                        (getHelp.Contains "--all")
                        "Non-page reads do not advertise page traversal")
        ]

[<Tests>]
let suggestionScope =
    testCase "CLI option suggestions use the selected command's advertised options" (fun () ->
        let errors arguments =
            match Grammar.prepare identity arguments with
            | Error issues -> issues
            | Ok _ -> failtest "A mistyped option must be refused"

        let page = errors [ "case"; "list"; "--al" ]

        Expect.contains
            page
            (UsageError.UnknownOption("--al", Some "--all"))
            "Page traversal is suggested on page commands"

        let single = errors [ "case"; "get"; "REFERENCE"; "--al" ]

        Expect.isFalse
            (single
             |> List.exists (function
                 | UsageError.UnknownOption(_, Some "--all") -> true
                 | _ -> false))
            "A single read never suggests unavailable page traversal")

[<Tests>]
let historyTraversal =
    testCase
        "CLI history help does not advertise traversal the authority CSV path cannot perform"
        (fun () ->
            let code, help = inspect [ "authority"; "history"; "--help" ]
            Expect.equal code 0 "Authority history has grammar-derived help"

            Expect.isFalse
                (help.Contains "--all")
                "The current authority exporter returns one page with a cursor")

[<Tests>]
let assignedLanguage =
    testCase "CLI command help accepts the ordinary assigned language form" (fun () ->
        Expect.equal
            (inspect [ "case"; "list"; "--help"; "--language=lv" ])
            (inspect [ "help"; "case"; "list"; "--language"; "lv" ])
            "The language flag has one meaning across discovery forms")

[<Tests>]
let unknownDefaults =
    testCase "CLI language defaults do not invent tokens in unknown-command diagnostics" (fun () ->
        for arguments in [ [ "frobnicate" ]; [ "case"; "frobnicate" ]; [ "-x" ] ] do
            Expect.equal
                (CliInvocations.admitConfigured arguments)
                (Ok(CliInvocation.Verb arguments))
                "Unknown command input stays authored until the usage error is rendered"

        let _, text = inspect [ "frobnicate"; "--language"; "lv" ]
        Expect.stringContains text "\"frobnicate\"" "Only the unknown root token is named")


[<Tests>]
let unknownSchema =
    testCase
        "unknown named schema lookup retains authored selectors with inert literal escapes"
        (fun () ->
            for selector, expected in
                [
                    "unsupported.synthetic", "unsupported.synthetic"
                    "bad\u001b[31m", "bad\\u{001B}[31m"
                    "line\nschema", "line\\u{000A}schema"
                ] do
                let code, text = inspect [ "schema"; selector ]
                Expect.equal code 64 "Unknown named schema is a usage refusal"

                Expect.stringContains
                    text
                    expected
                    "Actual unknown selector survives registry lookup failure"

                Expect.isFalse (text.Contains '\u001b') "No active terminal escape"

            for args in [ [ "schema" ]; [ "call"; "--help"; "extra" ] ] do
                Expect.equal
                    (inspect args |> fst)
                    64
                    "Missing selector and closed invocation remain usage failures"

            let admitted = CliInvocations.admit [ "schema"; "invocation" ]

            match admitted with
            | Ok(CliInvocation.Schema selection) ->
                use output = new MemoryStream()
                use errors = new MemoryStream()

                Expect.equal
                    (CliDiscovery.discovery output errors (CliInvocation.Schema selection))
                    0
                    "Known schema remains offline success"

                Expect.equal errors.Length 0L "Known schema has no diagnostics"

                let expected =
                    Discovery.schema selection
                    |> Option.defaultWith (fun () -> failtest "Known schema")

                Expect.equal
                    (output.ToArray())
                    (if expected.Length > 0 && expected[expected.Length - 1] = byte '\n' then
                         expected
                     else
                         Array.append expected [| byte '\n' |])
                    "Exact successful schema bytes remain unchanged"
            | other -> failtestf "Known schema admission: %A" other)


[<Tests>]
let failedDiscoveryLanguage =
    testCase "discovery loads its human failure language only when a lookup fails" (fun () ->
        use output = new MemoryStream()
        use errors = new MemoryStream()
        let mutable resolved = 0

        let language () =
            resolved <- resolved + 1
            "lv"

        let missing = UsageError.UnknownCommand("unknown-language-schema", [])
        let bytes = Encoding.UTF8.GetBytes "{}\n"

        Expect.equal
            (CliDiscovery.writeDiscovery output errors language "schema" missing (Some bytes))
            0
            "Successful machine output remains offline"

        Expect.equal
            (output.ToArray())
            bytes
            "Successful machine bytes are independent of language"

        Expect.equal resolved 0 "Success never loads the language/profile resolver"

        Expect.equal
            (CliDiscovery.writeDiscovery output errors language "schema" missing None)
            64
            "Failed lookup is a human usage refusal"

        Expect.equal resolved 1 "Only failure resolves language"
        let error = Encoding.UTF8.GetString(errors.ToArray())
        Expect.stringContains error "Nezināma komanda" "Independent Latvian failure expectation"
        Expect.stringContains error "unknown-language-schema" "Selector identity remains visible"
        use describeOutput = new MemoryStream()
        use describeErrors = new MemoryStream()

        Expect.equal
            (CliDiscovery.discovery
                describeOutput
                describeErrors
                (CliInvocation.CommandHelp([ "unsupported.synthetic" ], "lv", true)))
            64
            "Programmatic missing JSON description remains a refusal"

        Expect.stringContains
            (Encoding.UTF8.GetString(describeErrors.ToArray()))
            "Nezināma komanda"
            "Description failure preserves its already resolved language")
