module ClaimCore.Tests.ConfigurationHelpTests

open System
open System.IO
open Expecto
open ClaimCore.Catalog
open ClaimCore.Cli
open ClaimCore.HostSecurity
open ClaimCore.Wire

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
            CliHelp.text
                language
                topic
                Limits.MaximumInvocationBytes
                Limits.MaximumRequestBytes
                Limits.MaximumResponseBytes

        let content = rendered "configuration"

        Expect.equal
            (rendered InputDiagnostics.configurationId)
            content
            "Returned configuration ID maps to the existing topic"

        for literal in
            [
                "https://localhost/"
                "https://localhost"
                "https://localhost/path"
                "https://identity.example/realms/claimcore"
                "0600"
                "0640"
                "0440"
                "UTF-16"
                "NOT_SENT"
            ] do
            Expect.stringContains
                content
                literal
                "Exact documented forms and distinctions survive localization"

        Expect.equal
            (CliHelp.diagnosticDiscovery
                language
                InputDiagnostics.configurationId
                Limits.MaximumInvocationBytes
                Limits.MaximumRequestBytes
                Limits.MaximumResponseBytes)
            "ClaimCore.Cli help configuration\n"
            "Discovery points to the one topic rather than duplicating its essay"

        Expect.stringContains
            (rendered InputDiagnostics.protocolId)
            "NDJSON"
            "Configuration does not replace framing guidance"

[<Tests>]
let tests =
    testList
        "configuration repair help"
        [
            testCase "literal URI and client-ID forms follow current admission" literalForms
            testCase
                "documented private modes and distinct byte bounds use real admission"
                privateForms
            testCase "all localized configuration diagnostic aliases resolve to one topic" helpAlias
        ]
