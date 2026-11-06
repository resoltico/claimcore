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

        let rule input =
            let guidance = ScalarGuidance.forInput input

            let arguments =
                guidance.Arguments
                |> Map.map (fun _ value ->
                    match value with
                    | GuidanceArgument.Count count ->
                        count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    | GuidanceArgument.Code code -> code)

            Template.render english[guidance.Key] arguments

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
                        + escape (rule input.Name)
                        + " | "
                        + escape choices
                        + " | "
                        + escape defaultValue
                        + " |")
                yield!
                    CommandGrammar.collections operation
                    |> List.map (fun definition ->
                        let binding = definition.Binding

                        "| `"
                        + CommandGrammar.collectionFlag definition
                        + " "
                        + CommandGrammar.collectionPlaceholder definition
                        + "...` | "
                        + escape (message binding.LabelId)
                        + " — "
                        + escape (rule definition.Name)
                        + " | "
                        + string binding.MinimumItems
                        + "–"
                        + string binding.MaximumItems
                        + " | |")
                ""
            ]

        [
            "# CLI reference"
            ""
            "Generated from the semantic Protocol catalog and CLI command grammar. Regenerate with `dotnet run --project eng/ClaimCore.ProtocolGenerator -- --output contracts`; generator `--check` also checks this file."
            ""
            "A synopsis shows required inputs and brackets optional inputs. Choices use lowercase kebab spelling; exact uppercase wire tokens are also accepted. Mutation operations generate an operation ID when omitted. Keep that ID and the exact prepared frame for retries."
            ""
            "Use `--dry-run` to print the prepared JSON frame without authentication. This checks protocol shape and byte limits; domain admission, authorization and current revision are checked by the service. Use `--preview` with any case, work, reminder or authority act to request a review without recording it."
            ""
            "Value-taking verb options also accept `--name=value`; use `--text=--literal` when search text begins with `--`. Switches keep their valueless spelling."
            ""
            "Case worklist filters `--lifecycle`, `--status` and `--progress` may repeat to select a set. Omit a filter to include all values. `--text` searches current claimant, responsible insurer, case reference, incident description and handling summary; `--assignment mine|unassigned` selects responsibility within current access; `--due` selects actionable due state using the returned business-date basis; `--order reference|recent|notified|follow-up` selects the page order. Cursors bind all filters and order."
            "`case list --format csv`, `reminder list --format csv`, `case timeline --format csv` and `authority history --format csv` emit UTF-8 RFC 4180 CSV without a BOM, with wire-path headers and CRLF record endings. Null values are empty cells. `case list --all`, `case timeline --all` and `reminder list --all` follow continuation cursors until the end; each page is its own authorized snapshot. Concurrent ordering/filter changes can move, duplicate or omit rows; `--all` does not pin a snapshot export. Case-list text and CSV use one header; JSON emits one frame per page. A refusal or failure stops traversal and reports the cursor reached."
            "Timeline CSV emits one row per disclosed changed field, or a metadata-only row when no changes are disclosed. `changes.kind` distinguishes `DISCLOSED`, `WITHHELD` and `ERASED`. `field` uses core semantic names; reminder fields include the reminder ID. `before` and `after` contain canonical scalars: money is `amount CURRENCY`, and actor changes use ActorIds. Recorded actor names and name kinds are separate columns."
            "CSV is a spreadsheet-safe export: cells beginning with `=`, `+`, `-`, `@`, TAB or CR, including formula triggers after leading spaces/TABs, are prefixed with an apostrophe before quoting. This alters affected cells; use `--format json` for exact values."
            "Interactive text commands check authored syntax before any read. For existing-case changes, omitting `--expected-revision`, `--expected-work-revision` or `--expected-reminder-revision` reads the current entity and previews with its own revision. New-case OPEN previews without an existing-case lookup. Ordinary authority verbs preview after reading the authenticated context. A supplied operation ID is retained; otherwise one ID is allocated after valid syntax. Confirmation executes the exact reviewed request; declining prints Not recorded and exits 0. The service rechecks current permissions and state. Omitted correction groups mean KEEP; the review shows changed values and one Unchanged summary."
            ""
            "For existing-case changes, `--dry-run`, redirected input/output and `--format json` require an explicit revision. `--dry-run` stays offline and `--preview` remains preview-only; irreversible writes require exact target confirmation. Type the case reference for case ERASE, or the operation ID for ERASE_PROFILE and SETTLE. Redirected commands require `--confirm TARGET`; a mismatching target is a usage error. This local option is never sent in the wire request. Recovery settlement follows the same operation-ID rule."
            ""
            "Human data text visibly escapes terminal controls, line/paragraph separators and directional formatting as `\\u{XXXX}` (uppercase hexadecimal). Literal backslashes are doubled, so displayed escapes are distinct from exact stored values. Arabic text and joining characters remain readable. This applies to redirected text too; JSON/NDJSON retain exact decoded values and CSV keeps its export rules."
            "Narrative LF paragraph breaks are trusted layout around independently escaped data segments. Optional incident description and handling summary each admit 600 Unicode scalar values, preserving authored whitespace and spelling. Reminder action text is a separate 200-scalar single-line value. Empty or whitespace-only supplied text is invalid; omission keeps an existing value and `--clear-incident-description` or `--clear-handling-summary` explicitly clears it."
            "Use the field's `-file PATH`, `-stdin` or `-editor` option for longer text, for example `--handling-summary-file summary.txt` or `--action-file action.txt`. Sources are exclusive. File/stdin/editor input converts CRLF to LF before preparation; direct protocol CR is refused. User-supplied files are preserved. Editor entry requires a terminal and `VISUAL` or `EDITOR` naming one executable, without shell expressions or arguments. Owned temporary files/directories use 0600/0700 permissions; cleanup failure prevents request preparation and reports a diagnostic."
            "Work starts empty at revision zero after OPEN. Work edits preserve financial fact revisions; reminder edits preserve both fact and work revisions. `reminder get` reads one current record, including completed/cancelled records, using parent and reminder identities. `reminder list` defaults to pending actions on active open accessible cases. Due filtering there selects individual reminder dates; case `--due` uses the earliest pending reminder. Past-due reminders remain pending. No-plan attention is a warning, not a refusal or automatic reminder."
            "`reminder complete-and-plan-next` is one atomic two-effect operation: complete one pending reminder and create exactly one next pending reminder whose ID equals the operation ID. Human output refreshes the core work view after acceptance, including missing-plan attention; JSON/NDJSON contain only the requested structured reply. Initial OPEN and subsequent reminder planning are explicitly separate operations. Work overview counts cover accessible actionable cases independently of page/search and report their server date, zone and observation instant; suspended clients do not deliver alarms."
            "Human operations default to text. Add `--format json` for structured output or `--language en|lv|ar` to select interface text. Display language does not rewrite authored values. Human money, dates and observation footers use en-GB for English, lv-LV for Latvian and Latin-digit ar-AE for Arabic. Canonical JSON and CSV values are unchanged."
            ""
            "Top-level `claimcore` lists command families. `claimcore describe case open` describes an operation offline; `claimcore schema operation.execute request` shows its wire schema. `claimcore call` reads one JSON frame from stdin; `claimcore session` reads NDJSON."
            ""
            "`claimcore configure` atomically saves only public connection settings in `$XDG_CONFIG_HOME/claimcore/profile.json` (default `~/.config/claimcore/profile.json` on Unix). Files are 0600 and the profile directory is 0700. Every read uses private-file admission and refuses unknown members. Environment values override profile values individually; `CLAIMCORE_OIDC_CLIENT_SECRET_FILE` is environment-only. `claimcore configure --show` displays effective values and their sources without reading secret content."
            ""
            "`claimcore shell` requires interactive authentication mode and terminal stdin. It shares one process-local access/refresh-token session across commands, refreshes on expiry and signs in again on refresh failure. Fifteen idle minutes, exit, EOF and SIGTERM discard tokens. Arrow-key history stays in memory. Quotes and backslashes group arguments; metacharacters do not execute or expand. Each command prints its exit code and the shell continues after exit 4 with the frozen retry frame and recovery guidance. Ctrl-C cancels a command; a second Ctrl-C at an empty prompt exits. Interactive review still requires terminal stdout."
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
            yield! CommandGrammar.operations |> List.collect describe
        ]
        |> String.concat "\n"
        |> fun text -> Encoding.UTF8.GetBytes(text + "\n")
