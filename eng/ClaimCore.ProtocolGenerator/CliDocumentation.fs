namespace ClaimCore.ProtocolGeneration

open System.Text
open ClaimCore.Domain
open ClaimCore.Protocol
open ClaimCore.Cli

module CliDocumentation =
    let private argument (input: CatalogDerivedInput) =
        let name = CommandGrammar.inputName input.Name |> CommandGrammar.kebab

        match input.Argument with
        | CatalogArgument.Named -> "--" + name
        | CatalogArgument.Operand -> name.ToUpperInvariant()
        | CatalogArgument.Implicit -> ""

    let private exitMeaning =
        function
        | ExitCode.Success -> "Requested work completed."
        | ExitCode.Rejected -> "Input, usage or action was refused."
        | ExitCode.Failed -> "A known failure; earlier uncertain attempts remain unresolved."
        | ExitCode.OutcomeUnknown ->
            "Commit may have happened; retain the exact operation for recovery."
        | ExitCode.Diverged -> "Ledger verification found divergence."
        | ExitCode.Usage -> "CLI command usage was refused before dispatch."
        | ExitCode.InternalError -> "Unexpected CLI defect."
        | ExitCode.Cancelled -> "CLI interrupted or cancelled."

    let source () =
        let english = CatalogValidation.validate () |> Map.find "en"
        let message key = Template.render english[key] Map.empty

        let escape (text: string) =
            text.Replace("|", "\\|").Replace("\n", " ")

        let scalarGuidance (input: CatalogDerivedInput) =
            let guidance =
                match input.Name with
                | InteractionInput.Target(InputTarget.Field field) -> ScalarGuidance.forField field
                | (InteractionInput.Target InputTarget.Reason) -> Some ScalarGuidance.forReason
                | (InteractionInput.Target InputTarget.Text) -> Some ScalarGuidance.forSearchText
                | _ -> None

            guidance
            |> Option.map (fun guidance ->
                let arguments =
                    guidance.Arguments
                    |> Map.map (fun _ value ->
                        match value with
                        | GuidanceArgument.Count count ->
                            count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        | GuidanceArgument.Code code -> code)

                " " + Template.render english[guidance.Key] arguments)
            |> Option.defaultValue ""

        let describe operation =
            let inputs, _ = CommandGrammar.derive operation

            [
                "## " + (CommandGrammar.words operation |> String.concat " ")
                ""
                message (OperationCatalog.labelId operation)
                ""
                message (OperationCatalog.meaningId operation)
                ""
                "```sh"
                "claimcore " + CommandGrammar.synopsis operation
                "```"
                ""
                "| Input | Meaning | Choices | Default |"
                "| --- | --- | --- | --- |"
                yield!
                    inputs
                    |> List.filter (fun input -> input.Argument <> CatalogArgument.Implicit)
                    |> List.map (fun input ->
                        let choices =
                            match input.Atom with
                            | CatalogAtom.Choices choices
                            | CatalogAtom.Choice choices ->
                                choices |> List.map CommandGrammar.kebab |> String.concat ", "
                            | _ -> ""

                        let defaultValue =
                            match input.Binding.Default with
                            | Some(BindingValue.Integer value) -> string value
                            | Some(BindingValue.Text value) -> value
                            | Some(BindingValue.Boolean value) -> string value
                            | None -> ""

                        "| `"
                        + argument input
                        + "` | "
                        + escape (message input.Binding.LabelId)
                        + " — "
                        + escape (
                            (input.Binding.MeaningId
                             |> Option.map message
                             |> Option.defaultValue "")
                            + scalarGuidance input
                        )
                        + " | "
                        + escape choices
                        + " | "
                        + escape defaultValue
                        + " |")
                ""
            ]

        [
            "# CLI reference"
            ""
            "Generated from the semantic Protocol catalog and CLI command grammar. Regenerate with `dotnet run --project eng/ClaimCore.ProtocolGenerator -- --output contracts`; generator `--check` also checks this file."
            ""
            "A synopsis shows required inputs and brackets optional inputs. Choices use lowercase kebab spelling; exact uppercase wire tokens are also accepted. Mutation operations generate an operation ID when omitted. Keep that ID and the exact prepared frame for retries."
            ""
            "Use `--dry-run` to print the prepared JSON frame without authentication. This checks protocol shape and byte limits; domain admission, authorization and current revision are checked by the service. Use `--preview` with a case or authority act to request a review without recording it."
            ""
            "Value-taking verb options also accept `--name=value`; use `--text=--literal` when search text begins with `--`. Switches keep their valueless spelling."
            ""
            "Case worklist filters `--lifecycle`, `--status` and `--progress` may repeat to select a set. Omit a filter to include all values. `--text` searches claimant, responsible insurer and case reference; `--assignment assigned-to-me` restricts to valid case-scoped read grants; `--order reference|recent|notified` selects the page order. Cursors bind filters, assignment and order."
            "`case list --format csv` and `authority history --format csv` emit UTF-8 RFC 4180 CSV without a BOM, with wire-path headers and CRLF record endings. Null values are empty cells. `case list --all` follows continuation cursors until the end; each page is its own authorized snapshot. Text and CSV use one header; JSON emits one frame per page. A refusal or failure stops traversal and reports the cursor reached."
            "CSV is a spreadsheet-safe export: cells beginning with `=`, `+`, `-`, `@`, TAB or CR, including formula triggers after leading spaces/TABs, are prefixed with an apostrophe before quoting. This alters affected cells; use `--format json` for exact values."
            "When stdin and stdout are terminals and text output is selected, omitting `--expected-revision` on an existing-case act reads the case and previews with that revision, retaining a supplied operation ID or generating one when omitted, then asks for confirmation. Only an affirmative answer executes the same reviewed request. A concurrent change is still refused by the service. Omitted correction groups mean KEEP and the review shows every group action."
            ""
            "For existing-case changes, `--dry-run`, redirected input/output and `--format json` require an explicit revision. `--dry-run` stays offline and `--preview` remains preview-only; there is no skip-confirmation flag."
            ""
            "Human operations default to text. Add `--format json` for structured output or `--language en|lv|ar` to select interface text. Display language does not rewrite authored values."
            ""
            "Top-level `claimcore` lists command families. `claimcore describe case open` describes an operation offline; `claimcore schema operation.execute request` shows its wire schema. `claimcore call` reads one JSON frame from stdin; `claimcore session` reads NDJSON."
            ""
            "`claimcore configure` atomically saves only public connection settings in `$XDG_CONFIG_HOME/claimcore/profile.json` (default `~/.config/claimcore/profile.json` on Unix). Files are 0600 and the profile directory is 0700. Every read uses private-file admission and refuses unknown members. Environment values override profile values individually; `CLAIMCORE_OIDC_CLIENT_SECRET_FILE` is environment-only. `claimcore configure --show` displays effective values and their sources without reading secret content."
            ""
            "`claimcore shell` requires interactive authentication mode and terminal stdin. It shares one process-local access/refresh-token session across commands, refreshes on expiry and signs in again on refresh failure. Fifteen idle minutes, exit, EOF and SIGTERM discard tokens. Arrow-key history stays in memory. Quotes and backslashes group arguments; metacharacters do not execute or expand. Each command prints its exit code and the shell continues after exit 4 with the frozen retry frame and resolve command. Ctrl-C cancels a command; a second Ctrl-C at an empty prompt exits. Interactive review still requires terminal stdout."
            ""
            "## Discovery, configuration and frame commands"
            ""
            "Help, version, describe and schema commands work offline. Call and session dispatch frames through the configured HTTPS service and authentication mode."
            ""
            yield!
                CliCommand.all
                |> List.collect (
                    CliCommands.synopses >> List.map (fun syntax -> "- `claimcore " + syntax + "`")
                )
            ""
            "Help topics: "
            + (CliTopic.all
               |> List.map (fun topic -> "`" + CliTopic.token topic + "`")
               |> String.concat ", ")
            + "."
            ""
            "## Exit codes"
            ""
            "Both executables use the shared Protocol exit-code table. `claimcore-database help` reports its owner-command grammar without opening credentials or the database. Database refusals include a diagnostic; failures include a fault. Usage errors include the applicable synopsis."
            ""
            "| Exit | Meaning | claimcore | claimcore-database |"
            "| --- | --- | --- | --- |"
            yield!
                ExitCode.all
                |> List.map (fun code ->
                    let supported flag = if flag then "yes" else "—"

                    "| "
                    + string (ExitCode.value code)
                    + " | "
                    + exitMeaning code
                    + " | "
                    + supported (List.contains code CliIdentity.supportedExitCodes)
                    + " | "
                    + supported (List.contains code ClaimCore.Database.Command.supportedExitCodes)
                    + " |")
            ""
            yield! OperationCatalog.all |> List.collect describe
        ]
        |> String.concat "\n"
        |> fun text -> Encoding.UTF8.GetBytes(text + "\n")
