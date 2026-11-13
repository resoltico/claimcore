module ClaimCore.Tests.CliHelpTests

open System.Text
open Expecto
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Application.Contracts
open ClaimCore.Cli

let private descriptions () =
    for language in LocalizationPolicy.languages do
        for operation in CommandGrammar.operations do
            let words = CommandGrammar.words operation

            Expect.equal
                (CliInvocations.admit ([ "help" ] @ words @ [ "--language"; language ]))
                (Ok(CliInvocation.CommandHelp(words, language, false)))
                "Every declared verb resolves through help before rendering"

            let output = Discovery.commandHelp language words |> Option.get

            Expect.stringContains
                output
                (CommandGrammar.synopsis operation)
                "Description uses the operation grammar"

            let inputs = CommandGrammar.inputs operation

            for input in inputs do
                if input.Argument <> CommandArgument.Implicit then
                    Expect.stringContains
                        output
                        (ContractDiscovery.localized language (input.LabelId))
                        "Every admitted input has its localized label"

            Expect.isFalse (output.Contains "CommandInput") "Internal types do not leak into help"

[<Tests>]
let tests =
    testCase "every operation describes its actual inputs in all languages" descriptions

[<Tests>]
let semanticHelp =
    testCase
        "JSON command help supplies semantic task controls without wire paths or catalog aliases"
        (fun () ->
            let bytes = Discovery.commandHelpJson "en" [ "authority"; "admit" ] |> Option.get
            use document = System.Text.Json.JsonDocument.Parse(bytes: byte array)
            let operation = document.RootElement.GetProperty("operations")[0]

            Expect.isFalse
                (operation.TryGetProperty("catalog") |> fst)
                "Retired transport binding is not a compatibility output"

            let task = operation.GetProperty("task")

            Expect.equal
                (task.GetProperty("id").GetString())
                "AUTHORITY:ADMIT"
                "Semantic task identity"

            let collection = task.GetProperty("collections")[0]

            Expect.equal
                (collection.GetProperty("id").GetString())
                "authorityGrants"
                "Semantic collection"

            Expect.equal
                (collection.GetProperty("minimumRows").GetInt32())
                1
                "A compound admission includes a role"

            Expect.equal
                (collection.GetProperty("maximumRows").GetInt32())
                48
                "Authored collection bound"

            Expect.isFalse
                (document.RootElement
                    .GetRawText()
                    .Contains("\"path\"", System.StringComparison.Ordinal))
                "No member paths define controls"

            Expect.isFalse
                (document.RootElement
                    .GetRawText()
                    .Contains("\"branches\"", System.StringComparison.Ordinal))
                "No wire branch interpreter")

[<Tests>]
let legalNotice =
    testCase
        "offline CLI help carries public rights and the actual build source location"
        (fun () ->
            let text = ContractDiscovery.helpTopic "en" (CliTopic.Help HelpTopic.Overview)
            Expect.stringContains text "AGPL-3.0-only" "Public license is explicit"
            Expect.stringContains text "No warranty" "Warranty notice is visible"

            Expect.stringContains
                text
                "source for this build: SOURCE.tar.gz"
                "Source is adjacent to the published application"

            Expect.stringContains
                text
                "PROJECT-NOTICE.md"
                "Separate project terms remain accessible")

[<Tests>]
let documentedHelpForms =
    testCase
        "documented case and nested authority verb help resolves without a new grammar path"
        (fun () ->
            for words in [ [ "case"; "open" ]; [ "authority"; "requests"; "dismiss" ] ] do
                Expect.equal
                    (CliInvocations.admit ("help" :: words))
                    (Ok(CliInvocation.CommandHelp(words, "en", false)))
                    "Existing documented form is admitted"

                Expect.isSome (Discovery.commandHelp "en" words) "Existing command help renders")


[<Tests>]
let dryRunUsage =
    testCase "verb help shows the dry-run usage once, labelled, and only when it differs" (fun () ->
        let usageLines words =
            Discovery.commandHelp "en" words
            |> Option.get
            |> fun (text: string) -> text.Split '\n'
            |> Array.filter (fun line ->
                line.Contains("claimcore " + String.concat " " words + " "))

        let opening = usageLines [ "case"; "open" ]
        Expect.equal opening.Length 1 "Identical execution and dry-run usage is shown once"

        let deciding = usageLines [ "case"; "decide" ]
        Expect.equal deciding.Length 2 "A differing dry-run usage is shown in addition"

        Expect.isTrue
            (deciding[1].StartsWith "With --dry-run: claimcore case decide")
            "The dry-run usage is labelled"

        Expect.stringContains
            deciding[1]
            "[--expected-revision N]"
            "Dry-run admits an omitted revision")

[<Tests>]
let usageRequiredness =
    testCase "usage reflects executable and draft requirements and lists schema exports" (fun () ->
        let erase = TaskId.Operation(ActKind.Case CaseActKind.Erase)
        let execute = CommandGrammar.synopsis erase

        Expect.stringContains
            execute
            "[--operation-id VALUE]"
            "The CLI allocates an omitted identity"

        Expect.isFalse
            (execute.Contains "[--confirm")
            "Irreversible execution requires confirmation"

        Expect.isFalse
            (execute.Contains "[--expected-revision")
            "Direct execution requires a revision"

        let draft = CommandGrammar.synopsisForDraft true erase

        Expect.stringContains
            draft
            "[--expected-revision N]"
            "Dry-run admits an incomplete revision"

        Expect.stringContains draft "[--confirm TARGET]" "Dry-run does not execute"

        let usage =
            UsageText.renderHost "en" [ "schema" ] (UsageError.MissingOperand "SCHEMA")

        let help = ContractDiscovery.helpTopic "en" (CliTopic.Help HelpTopic.Schema)

        for name in CliSchemas.names do
            Expect.stringContains usage name "Missing schema lists every named export"
            Expect.stringContains help name "Schema help lists every named export"
            Expect.isSome (ContractDiscovery.schema name) "Every advertised export resolves")
