module ClaimCore.Tests.ConfigurationHelpTests

open System
open System.IO
open Expecto
open ClaimCore.Domain
open ClaimCore.Protocol
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

        Expect.stringContains
            (rendered (CliTopic.Diagnostic CliGuidance.ProtocolGuidance))
            "NDJSON"
            "Configuration does not replace framing guidance"

let private operatorPhrases =
    [
        "en",
        "accepted:",
        "refused:",
        "accepted syntax:",
        "(missing explicit /)",
        "(not a service root)",
        "Surrounding whitespace is trimmed before the secret is sent."
        "lv",
        "pieņemts:",
        "atteikts:",
        "pieņemta sintakse:",
        "(trūkst skaidri norādīta /)",
        "(nav pakalpojuma sakne)",
        "Pirms nosūtīšanas noslēpumam tiek noņemtas apkārtējās atstarpes."
        "ar",
        "مقبول:",
        "مرفوض:",
        "صياغة مقبولة:",
        "(علامة / الصريحة مفقودة)",
        "(ليس جذر الخدمة)",
        "تُزال المسافات المحيطة بالسر قبل إرساله."
    ]

let private localizedExplanations () =
    let render language =
        ContractDiscovery.helpTopic language (CliTopic.Help HelpTopic.Configuration)

    for language, accepted, refused, syntax, missingSlash, notRoot, trim in operatorPhrases do
        let content = render language

        for expected in
            [
                "CLAIMCORE_SERVICE_URL " + accepted + " https://localhost/"
                "CLAIMCORE_SERVICE_URL " + refused + " https://localhost " + missingSlash
                "CLAIMCORE_SERVICE_URL " + refused + " https://localhost/path " + notRoot
                "CLAIMCORE_OIDC_ISSUER " + syntax + " https://identity.example/realms/claimcore"
                trim
            ] do
            Expect.stringContains
                content
                expected
                "Operator prose is localized while technical examples remain exact"

        for historical in
            [
                "Existing whitespace treatment is unchanged"
                "Esošā atstarpju apstrāde nemainās"
                "معالجة المسافات الحالية لا تتغير"
            ] do
            Expect.isFalse
                (content.Contains historical)
                "Help describes current behavior rather than development history"

        if language <> "en" then
            for english in
                [
                    "accepted:"
                    "refused:"
                    "accepted syntax:"
                    "(missing explicit /)"
                    "(not a service root)"
                ] do
                Expect.isFalse
                    (content.Contains english)
                    "Non-English help cannot retain untranslated example explanations"

        Expect.equal
            (ContractDiscovery.helpTopic
                language
                (CliTopic.Diagnostic CliGuidance.ConfigurationIssue))
            content
            "Diagnostic alias retains identical localized rules"

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
