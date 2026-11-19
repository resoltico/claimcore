module ClaimCore.Tests.ConfigurationHelpTests

open System
open System.IO
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Cli
open ClaimCore.HostSecurity
open ClaimCore.CommandLine

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
            ContractDiscovery.helpTopic (RenderingContext.forLanguage language) topic

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
        ContractDiscovery.helpTopic
            (RenderingContext.forLanguage "en")
            (CliTopic.Help HelpTopic.Configuration)

    Expect.stringContains english "Secrets are never saved" "Current secret handling stays visible."

    for language in LocalizationPolicy.languages |> List.filter ((<>) "en") do
        let content =
            ContractDiscovery.helpTopic
                (RenderingContext.forLanguage language)
                (CliTopic.Help HelpTopic.Configuration)

        Expect.notEqual
            content
            english
            "Localized help must not fall back to the full English topic."

        Expect.stringContains
            content
            "claimcore configure --show"
            "The actionable command remains exact."

let private configureLines () =
    for language in [ "en"; "lv"; "ar"; "ko"; "zh-Hans"; "zh-Hant"; "ja"; "es"; "de" ] do
        let rendering = RenderingContext.forLanguage language
        let topic = CliTopic.Help HelpTopic.Configure
        let rendered = ContractDiscovery.helpTopic rendering topic

        let firstCommand =
            rendered.IndexOf("\nclaimcore configure --show\n", StringComparison.Ordinal)

        Expect.isGreaterThan firstCommand 0 "The configure synopsis follows its explanatory body"
        let body = rendered[.. firstCommand - 1]
        let lines = body.Split('\n')
        Expect.hasLength lines 6 "The common heading and five authored body lines"

        Expect.isTrue
            (lines |> Array.forall (String.IsNullOrWhiteSpace >> not))
            "No empty body lines"

        Expect.isFalse
            (rendered.Contains "\\n")
            "The rendered text contains no literal backslash escape"

        use help =
            System.Text.Json.JsonDocument.Parse(
                Discovery.helpJson rendering "configure" rendered []
            )

        Expect.equal
            (help.RootElement.GetProperty("text").GetString())
            rendered
            "Decoded help preserves actual line breaks"

let private nativeAcquisition () =
    let id () = "11111111-2222-7333-8444-555555555555"
    let opening = [ "case"; "open"; "CASE-EXAMPLE" ]
    let privateValue = "private-party-canary"

    for arguments, expected, machine in
        [
            [ "case"; "get"; "--case-id"; "wrong" ],
            UsageError.InvalidUuid "--case-id",
            "INVALID_UUID"
            [ "reminder"; "list"; "--include-inactive=maybe" ],
            UsageError.InvalidBoolean "--include-inactive",
            "INVALID_BOOLEAN"
            [ "case"; "list"; "--limit"; "fraction" ],
            UsageError.InvalidInteger("--limit", "fraction"),
            "INVALID_INTEGER"
            [
                "case"
                "close"
                "CASE-EXAMPLE"
                "--outcome"
                "OTHER"
                "--expected-revision"
                "18446744073709551616"
            ],
            UsageError.InvalidRange(
                "--expected-revision",
                "18446744073709551616",
                1L,
                9007199254740991L
            ),
            "INVALID_RANGE"
            [
                "case"
                "record-payment"
                "CASE-EXAMPLE"
                "--expected-revision"
                "1"
                "--direction"
                "WRONG"
                "--payment-date"
                "2026-11-18"
                "--paid-amount"
                "1"
                "--paid-currency"
                "EUR"
            ],
            UsageError.InvalidChoice("--direction", "WRONG", [ "paid"; "returned" ]),
            "INVALID_CHOICE"
            opening @ [ "--party"; "{ " + privateValue ],
            UsageError.InvalidCollection("--party", CollectionProblem.MalformedJson),
            "INVALID_COLLECTION"
            opening
            @ [
                "--party"
                "{\"input:partyName\":\"" + privateValue + "\",\"input:partyName\":\"Again\"}"
            ],
            UsageError.InvalidCollection("--party", CollectionProblem.DuplicateMember),
            "INVALID_COLLECTION"
            opening @ [ "--party"; "[]" ],
            UsageError.InvalidCollection("--party", CollectionProblem.ExpectedObject),
            "INVALID_COLLECTION"
            opening @ [ "--party"; "{\"private\":\"" + privateValue + "\"}" ],
            UsageError.InvalidCollection("--party", CollectionProblem.UnknownMember),
            "INVALID_COLLECTION"
            opening @ [ "--party"; """{"input:partyRole":true}""" ],
            UsageError.InvalidCollection("--party", CollectionProblem.InvalidMember),
            "INVALID_COLLECTION"
            opening @ (List.replicate 21 [ "--party"; "{}" ] |> List.concat),
            UsageError.InvalidCollectionCount("--party", 0, 20),
            "INVALID_COLLECTION_COUNT"
        ] do
        match Grammar.prepare id arguments with
        | Error [ issue ] ->
            Expect.equal issue expected "Parse the offending value at its acquisition boundary"
            let raw = ClaimCore.Json.Codec.encode UsageFrame.codec { Issue = issue; Usage = [] }
            use frame = System.Text.Json.JsonDocument.Parse raw

            Expect.equal
                (frame.RootElement.GetProperty("issue").GetProperty("kind").GetString())
                machine
                "Closed machine diagnostic code"

            if machine = "INVALID_RANGE" then
                let encoded = frame.RootElement.GetProperty "issue"
                Expect.equal (encoded.GetProperty("minimum").GetInt64()) 1L "Machine minimum"

                Expect.equal
                    (encoded.GetProperty("maximum").GetInt64())
                    9007199254740991L
                    "Full-width machine maximum"

            Expect.isFalse
                ((System.Text.Encoding.UTF8.GetString raw).Contains privateValue)
                "The machine error omits private row contents"

            Expect.isFalse
                ((UsageText.render "en" arguments [ issue ]).Contains privateValue)
                "The text error omits private row contents"
        | other -> failtestf "Expected one typed usage error, received %A" other

    let unrelated =
        UsageText.render "en" [] [ UsageError.InvalidValue("--cursor", "example") ]

    Expect.isFalse
        (unrelated.Contains "whole-number")
        "A generic failure must not borrow integer wording"

let private authoredHelp () =
    let rendering = RenderingContext.forLanguage "en"

    let topic words =
        Discovery.commandHelpJson rendering words |> Option.get

    let operation (help: System.Text.Json.JsonDocument) =
        help.RootElement.GetProperty("operations")[0]

    let offline arguments =
        CliOffline.invoke arguments (System.Collections.Generic.Dictionary<string, string>()) None

    use help = System.Text.Json.JsonDocument.Parse(topic [ "case"; "open" ])
    let rows = (operation help).GetProperty("task").GetProperty("collections")[0]
    let members = rows.GetProperty("members").EnumerateArray() |> Seq.toList

    Expect.equal
        (members
         |> List.map (fun row ->
             row.GetProperty("id").GetString(), row.GetProperty("required").GetBoolean()))
        [
            "input:partyRole", true
            "input:partyName", false
            "input:partyDetails", false
        ]
        "Party help supplies the actual row grammar"

    Expect.equal
        (members.Head.GetProperty("choices").EnumerateArray()
         |> Seq.map _.GetString()
         |> Set.ofSeq)
        (set [ "CLAIMANT"; "INSURER"; "REPRESENTATIVE"; "OTHER" ])
        "Choices are exact JSON tokens"

    let helpText = help.RootElement.GetProperty("text").GetString() |> nonNull

    for token in [ "input:partyRole"; "[input:partyName]"; "[input:partyDetails]"; "CLAIMANT" ] do
        Expect.stringContains helpText token "Text help exposes the same grammar"

    let row = rows.GetProperty("example").GetString() |> nonNull

    let result =
        offline
            [
                "case"
                "open"
                "CASE-EXAMPLE"
                "--party"
                row
                "--dry-run"
                "--format"
                "json"
            ]

    Expect.equal result.ExitCode 0 "The printed example passes the actual offline command"

    use payment =
        System.Text.Json.JsonDocument.Parse(topic [ "case"; "record-payment" ])

    let direction =
        (operation payment).GetProperty("task").GetProperty("inputs").EnumerateArray()
        |> Seq.find (fun input -> input.GetProperty("id").GetString() = "input:direction")

    Expect.isFalse
        (direction.GetProperty("required").GetBoolean())
        "The CLI direction flag is optional"

    Expect.equal
        (direction.GetProperty("mode").GetString())
        "REQUIRED"
        "Core still requires direction"

    Expect.equal
        (direction.GetProperty("default").GetString())
        "PAID"
        "The declared default is visible"

    Expect.stringContains
        ((operation payment).GetProperty("synopsis").GetString() |> nonNull)
        "[--direction"
        "Text syntax shows that direction can be omitted"

    let baseArgs =
        [
            "case"
            "record-payment"
            "CASE-EXAMPLE"
            "--expected-revision"
            "1"
            "--payment-date"
            "2026-11-18"
            "--paid-amount"
            "1"
            "--paid-currency"
            "EUR"
            "--dry-run"
            "--format"
            "json"
        ]

    for args, direction in [ [], "PAID"; [ "--direction"; "RETURNED" ], "RETURNED" ] do
        let result = offline (baseArgs @ args)
        Expect.equal result.ExitCode 0 "Offline authoring admits the selected direction"
        use frame = System.Text.Json.JsonDocument.Parse result.StandardOutput

        Expect.equal
            (frame.RootElement
                .GetProperty("input")
                .GetProperty("inputs")
                .GetProperty("input:direction")
                .GetString())
            direction
            "The prepared input retains the exact direction"

    for value in [ ""; "WRONG" ] do
        let result = offline (baseArgs @ [ "--direction"; value ])
        Expect.notEqual result.ExitCode 0 "Explicit invalid values cannot become defaults"

[<Tests>]
let tests =
    testList
        "configuration repair help"
        [
            testCase
                "configure help supplies five actual body lines in all languages"
                configureLines
            testCase
                "native typed diagnostics preserve bounds and redact row contents"
                nativeAcquisition
            testCase
                "native party help and payment default match actual offline authoring"
                authoredHelp
            testCase
                "operator explanations are localized and state current secret handling"
                localizedExplanations
            testCase "literal URI and client-ID forms follow current admission" literalForms
            testCase
                "documented private modes and distinct byte bounds use real admission"
                privateForms
            testCase "all localized configuration diagnostic aliases resolve to one topic" helpAlias
        ]
