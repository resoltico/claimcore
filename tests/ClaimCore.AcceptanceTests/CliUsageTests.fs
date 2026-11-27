module ClaimCore.AcceptanceTests.CliUsageTests

open System
open System.IO
open System.Text
open System.Collections.Generic
open Expecto
open ClaimCore.TestSupport

[<Tests>]
let smoke =
    testCase "published CLI offline usage help schema and dry-run" (fun () ->
        let directory = Directory.CreateTempSubdirectory "claimcore-offline-smoke-"

        try
            let cli =
                PublishedProcesses.publish
                    (RepositoryRoot.find ())
                    directory.FullName
                    "ClaimCore.Cli"

            let caseId = "10000000-0000-7000-8000-000000000001"

            for arguments, expectedCode, expectedText in
                [
                    [ "case"; "close"; "--case-id"; caseId; "--outcome"; "other" ],
                    64,
                    "--expected-revision"
                    [ "help"; "case"; "open" ], 0, "Open a case"
                    [ "schema"; "invocation" ], 0, "endpoint"
                    [ "case"; "get"; "--case-id"; caseId; "--dry-run" ], 0, "case.get"
                ] do
                let result =
                    ProcessRunner.dotnet 8_000 cli arguments (Dictionary<string, string>()) None

                Expect.equal result.ExitCode expectedCode "Published offline exit status"

                let bytes =
                    if expectedCode = 0 then
                        result.StandardOutput
                    else
                        result.StandardError

                Expect.stringContains
                    (Encoding.UTF8.GetString bytes)
                    expectedText
                    "Published offline content"

                Expect.isEmpty
                    (if expectedCode = 0 then
                         result.StandardError
                     else
                         result.StandardOutput)
                    "Only the intended stream"

            let recover arguments =
                let apphost =
                    if OperatingSystem.IsWindows() then
                        Path.ChangeExtension(cli, ".exe")
                    else
                        Path.ChangeExtension(cli, null)

                let start =
                    PublishedProcesses.start
                        apphost
                        ("recovery" :: arguments)
                        (Dictionary<string, string>())

                CliProcessEnvironment.isolateProfile start (Path.GetDirectoryName cli |> nonNull)
                start.Environment["CLAIMCORE_LANGUAGE"] <- "en"
                start.Environment["NO_COLOR"] <- "1"
                PublishedProcesses.run 8_000 start None

            let invalidRecovery arguments text synopsis =
                let result = recover arguments
                let diagnostic = Encoding.UTF8.GetString result.StandardError
                Expect.equal result.ExitCode 64 "Malformed recovery input fails as usage"
                Expect.isEmpty result.StandardOutput "Recovery usage diagnostics stay on stderr"

                Expect.stringContains
                    diagnostic
                    text
                    "The typed recovery diagnostic survives admission"

                Expect.stringContains diagnostic synopsis "Use the recovery synopsis"

                Expect.isFalse
                    (diagnostic.Contains "accepts no options or operands")
                    "Recovery admits options"

                Expect.isFalse
                    (diagnostic.Contains "Unknown option")
                    "A recognized recovery option retains its identity"

                diagnostic

            let invalidAttempt = "private-malformed-attempt"

            let uuid =
                invalidRecovery
                    [ "check"; invalidAttempt; "--format"; "json"; "--language"; "en" ]
                    "Use a UUID in hyphenated form for ATTEMPT_ID."
                    "claimcore recovery check ATTEMPT_ID"

            Expect.isFalse
                (uuid.Contains invalidAttempt)
                "A malformed attempt identifier is redacted"

            invalidRecovery
                [ "list"; "--display-locale"; "invalid-locale"; "--language"; "en" ]
                "Invalid value invalid-locale for --display-locale"
                "claimcore recovery list"
            |> ignore

            for flag, value in
                [ "--language", "synthetic-language"; "--format", "synthetic-format" ] do
                for arguments in [ [ flag; value ]; [ flag + "=" + value ] ] do
                    invalidRecovery
                        ("list" :: arguments)
                        ("Invalid value " + value + " for " + flag)
                        "claimcore recovery list"
                    |> ignore

                let nextOption =
                    if flag = "--language" then
                        "--format=json"
                    else
                        "--language=en"

                for arguments in [ [ flag ]; [ flag; nextOption ] ] do
                    invalidRecovery
                        ("list" :: arguments)
                        ("Missing value for " + flag + ".")
                        "claimcore recovery list"
                    |> ignore

            for arguments in [ [ "list" ]; [ "check"; "70000000-0000-4000-8000-000000000001" ] ] do
                let result =
                    recover (
                        arguments
                        @ [ "--display-locale"; "en-US"; "--language"; "en"; "--format=json" ]
                    )

                Expect.equal
                    result.ExitCode
                    3
                    "Admitted recovery reaches the isolated configuration boundary"

                Expect.isEmpty result.StandardError "An admitted JSON recovery uses stdout"
                use reply = Text.Json.JsonDocument.Parse result.StandardOutput

                Expect.equal
                    (reply.RootElement.GetProperty("delivery").GetString())
                    "NOT_SENT"
                    "No service configured"

                Expect.equal
                    (reply.RootElement
                        .GetProperty("result")
                        .GetProperty("localFailure")
                        .GetProperty("code")
                        .GetString())
                    "CONFIGURATION_INVALID"
                    "The valid recovery grammar reaches missing service configuration"
        finally
            directory.Delete true)

[<Tests>]
let redirectedShell =
    testCase
        "published shell rejects redirected stdin as localized usage without a JSON frame"
        (fun () ->
            let directory = Directory.CreateTempSubdirectory "claimcore-shell-usage-"

            try
                let cli =
                    PublishedProcesses.publish
                        (RepositoryRoot.find ())
                        directory.FullName
                        "ClaimCore.Cli"

                let result =
                    ProcessRunner.dotnet
                        10_000
                        cli
                        [ "shell" ]
                        (Dictionary<string, string>())
                        (Some(Text.Encoding.UTF8.GetBytes "{}\n"))

                Expect.equal result.ExitCode 64 "Terminal misuse is a usage error"
                Expect.isEmpty result.StandardOutput "No JSON frame is emitted"
                let text = Text.Encoding.UTF8.GetString result.StandardError
                Expect.stringContains text "interactive terminal" "Explain the required terminal"
                Expect.stringContains text "batch" "Explain the piped alternative"

                Expect.isFalse
                    (text.Contains("CONFIGURATION_INVALID"))
                    "Do not misdiagnose usage as configuration"
            finally
                directory.Delete true)
