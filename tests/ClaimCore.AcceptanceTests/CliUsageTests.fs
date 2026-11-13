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

            for arguments, expectedCode, expectedText in
                [
                    [ "case"; "close"; "REF" ], 64, "--expected-revision"
                    [ "help"; "case"; "open" ], 0, "Open a case"
                    [ "schema"; "invocation" ], 0, "endpoint"
                    [ "case"; "get"; "REF"; "--dry-run" ], 0, "case.get"
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
                Expect.stringContains text "session" "Explain the piped alternative"

                Expect.isFalse
                    (text.Contains("CONFIGURATION_INVALID"))
                    "Do not misdiagnose usage as configuration"
            finally
                directory.Delete true)
