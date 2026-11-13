module ClaimCore.Tests.CliOfflineDiscoveryTests

open ClaimCore.CommandLine

open System
open System.IO
open System.Text
open System.Text.Json
open System.Collections.Generic
open Expecto
open ClaimCore.Domain
open ClaimCore.Cli

[<Tests>]
let tests =
    testCase "offline CLI discovery aliases and escaped selectors" (fun () ->
        let invoke args input =
            CliOffline.invoke args (Dictionary<string, string>()) input
        // Exercise dispatcher output in addition to the complete declaration matrix.
        for language in [ "en" ] do
            for topic in CliTopic.all do
                let result = invoke [ "help"; CliTopic.token topic; "--language"; language ] None

                Expect.equal result.ExitCode 0 "All topics work offline"
                Expect.equal result.StandardError.Length 0 "Human help has no errors"
                Expect.isGreaterThan result.StandardOutput.Length 20 "Useful offline help"

        let machineHelp = invoke [ "help"; "exit-codes"; "--format"; "json" ] None
        Expect.equal machineHelp.ExitCode 0 "Machine help works offline"
        use helpDocument = JsonDocument.Parse machineHelp.StandardOutput
        let codes = helpDocument.RootElement.GetProperty("exitCodes")
        Expect.equal (codes.GetArrayLength()) 7 "Published exit-code topic contains every status"
        Expect.equal (codes[4].GetProperty("value").GetInt32()) 64 "Usage exit status"

        for commandName in CliCommand.all do
            let command = CliCommand.token commandName

            for alias in [ "--help"; "-h" ] do
                let result = invoke [ command; alias ] None
                Expect.equal result.ExitCode 0 "Alias never opens stdin/config/network"
                let canonical = invoke [ "help"; command ] None

                Expect.equal
                    result.StandardOutput
                    canonical.StandardOutput
                    "Alias emits canonical help bytes"

        let canary = "secret-canary\u001b[31mhttps://private.example/path"

        for args, echoSelector in
            [
                [ canary ], true
                [ "help"; canary ], true
                [ "call"; "--help"; canary ], false
                [ "help"; "fields"; canary ], true
                [ "schema"; canary ], true
            ] do
            let result = invoke args None
            Expect.equal result.ExitCode 64 "Usage failure"
            Expect.equal result.StandardOutput.Length 0 "No machine stdout on invalid invocation"

            let error = Encoding.UTF8.GetString result.StandardError
            Expect.isFalse (error.Contains '\u001b') "Usage never emits an active ANSI escape"
            Expect.isFalse (error.Contains canary) "The raw control-bearing token is never echoed"

            if echoSelector then
                Expect.stringContains
                    error
                    "secret-canary\\u{001B}[31mhttps://private.example/path"
                    "The authored unknown selector is retained as independent inert text"
            else
                Expect.isFalse
                    (error.Contains "secret-canary")
                    "Closed call invocation keeps its existing argument refusal"

    )
