module ClaimCore.Tests.ConfigurationHelpTests

open System
open System.IO
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.HostSecurity

let private create service issuer clientId =
    RemoteConfiguration.create service issuer clientId RemoteClientMode.Interactive None

let private literalForms () =
    for service, valid in
        [
            "https://localhost/", true
            "https://localhost", false
            "https://localhost/path", false
        ] do
        Expect.equal
            (create service "https://identity.example/realms/claimcore" "synthetic"
             |> Result.isOk)
            valid
            "Exact help service examples use the existing parser"

    Expect.isOk
        (create "https://localhost/" "https://identity.example/realms/claimcore" "synthetic")
        "Issuer path has distinct admission"

    for value in [ ""; " "; " synthetic"; "synthetic " ] do
        Expect.isError
            (create "https://localhost/" "https://identity.example/realms/claimcore" value)
            "No independent client-ID counting/trim policy"

    Expect.isOk
        (create
            "https://localhost/"
            "https://identity.example/realms/claimcore"
            (String('x', CliConfigurationLimits.MaximumClientIdCodeUnits)))
        "Existing boundary includes maximum"

    Expect.isError
        (create
            "https://localhost/"
            "https://identity.example/realms/claimcore"
            (String('x', CliConfigurationLimits.MaximumClientIdCodeUnits + 1)))
        "Existing boundary refuses excess"

let private privateForms () =
    let directory = Directory.CreateTempSubdirectory("claimcore-configuration-help-")

    try
        let path = Path.Combine(directory.FullName, "synthetic")

        for mode in
            [
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite
                UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.GroupRead
                UnixFileMode.UserRead ||| UnixFileMode.GroupRead
            ] do
            File.WriteAllBytes(path, Array.create CliConfigurationLimits.MaximumSecretBytes 42uy)
            File.SetUnixFileMode(path, mode)

            Expect.isOk
                (PrivateFile.read CliConfigurationLimits.MaximumSecretBytes path)
                "The documented sufficient/permitted modes use actual private admission"

            Expect.equal
                (PrivateFile.read (CliConfigurationLimits.MaximumSecretBytes - 1) path)
                (Error PrivateFileFailure.TooLarge)
                "Caller bound rejects excess"

        File.SetUnixFileMode(path, UnixFileMode.UserRead ||| UnixFileMode.UserWrite)
        File.WriteAllBytes(path, Array.create CliConfigurationLimits.MaximumTrustRootBytes 42uy)

        Expect.isOk
            (PrivateFile.read CliConfigurationLimits.MaximumTrustRootBytes path)
            "Trust-root byte admission maximum"

        Expect.equal
            (PrivateFile.read (CliConfigurationLimits.MaximumTrustRootBytes - 1) path)
            (Error PrivateFileFailure.TooLarge)
            "Trust-root byte bound remains distinct"
    finally
        directory.Delete true

let private helpAlias () =
    for language in LocalizationPolicy.languages do
        let rendered topic =
            ContractDiscovery.helpTopic language topic

        let content = rendered (CliTopic.Help HelpTopic.Configuration)

        Expect.equal
            (rendered (CliTopic.Diagnostic CliGuidance.ConfigurationIssue))
            content
            "Returned configuration ID maps to the existing topic"

        Expect.stringContains
            content
            "docs/integrating.md#cli-use"
            "Exact configuration rules live in the task guide."

        let lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        Expect.isLessThanOrEqual lines.Length 5 "Four help lines plus the common heading."

        Expect.stringContains
            (rendered (CliTopic.Diagnostic CliGuidance.ProtocolGuidance))
            "session"
            "The shared call/session topic explains framing."

let private localizedExplanations () =
    let english =
        ContractDiscovery.helpTopic "en" (CliTopic.Help HelpTopic.Configuration)

    Expect.stringContains english "Secrets are never saved" "Current secret handling stays visible."

    for language in LocalizationPolicy.languages |> List.filter ((<>) "en") do
        let content =
            ContractDiscovery.helpTopic language (CliTopic.Help HelpTopic.Configuration)

        Expect.notEqual
            content
            english
            "Localized help must not fall back to the full English topic."

        Expect.stringContains
            content
            "claimcore configure --show"
            "The actionable command remains exact."

[<Tests>]
let tests =
    testList
        "configuration repair help"
        [
            testCase
                "operator explanations are localized and state current secret handling"
                localizedExplanations
            testCase "literal URI and client-ID forms follow current admission" literalForms
            testCase
                "documented private modes and distinct byte bounds use real admission"
                privateForms
            testCase "all localized configuration diagnostic aliases resolve to one topic" helpAlias
        ]
