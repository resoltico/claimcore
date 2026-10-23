module ClaimCore.Tests.CliInvocationTests

open System
open System.Text
open System.Text.Json.Nodes
open Expecto
open ClaimCore.Contract
open ClaimCore.Contract.Protocol
open ClaimCore.Application

let private aliases () =
    for command in CliHelp.commands do
        for alias in [ "--help"; "-h" ] do
            Expect.equal
                (CliInvocations.admit [ command; alias ])
                (Ok(CliInvocation.Help(command, "en")))
                "Offline exact aliases"

            Expect.isError
                (CliInvocations.admit [ command; alias; "--language"; "lv" ])
                "Extra operands never dispatch"

    for language in LocalizationPolicy.languages do
        for topic in CliHelp.topics do
            Expect.equal
                (CliInvocations.admit [ "help"; topic; "--language"; language ])
                (Ok(CliInvocation.Help(topic, language)))
                "Canonical localized help"

            Expect.isGreaterThan
                (CliHelp.text
                    language
                    topic
                    Limits.MaximumInvocationBytes
                    Limits.MaximumRequestBytes
                    Limits.MaximumResponseBytes)
                    .Length
                100
                "Useful guidance"

let private options () =
    Expect.equal
        (CliInvocations.admit [ "call"; "--retry" ])
        (Ok(CliInvocation.Call true))
        "Retry context is explicit and leaves the request frame unchanged."

    Expect.equal
        (CliInvocations.admit [ "call" ])
        (Ok(CliInvocation.Call false))
        "An ordinary call starts a fresh attempt."

    for args in
        [
            [ "call"; "--format"; "text" ]
            [ "describe"; "--language"; "lv" ]
            [ "schema"; "invocation"; "--language"; "en" ]
            [ "help"; "call"; "--language"; "en"; "--language"; "en" ]
            [ "help"; "secret-canary" ]
            [ "describe"; "secret-canary" ]
            [ "describe"; "summary"; "extra" ]
            [ "describe"; "commands"; "OPEN"; "extra" ]
            [ "schema"; "context"; "extra" ]
            [ "describe"; "--format"; "text"; "--format"; "text" ]
        ] do
        Expect.isError (CliInvocations.admit args) "Unsupported combinations refuse atomically"

    Expect.equal
        (CliInvocations.admit
            [ "describe"; "commands"; "OPEN"; "--format"; "text"; "--language"; "ar" ])
        (Ok(CliInvocation.Describe(CliDescription.Commands(Some "OPEN"), true, "ar")))
        "Explicit human format"

    Expect.isOk
        (Frames.admit (Encoding.UTF8.GetBytes "{\"endpoint\":\"context\",\"input\":{}}"))
        "Help example is admitted"

[<Tests>]
let tests =
    testList
        "CLI invocation admission"
        [
            testCase "exact help aliases and all localized topics" aliases
            testCase "strict human versus machine option combinations" options
        ]
