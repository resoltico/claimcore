namespace ClaimCore.ContractGeneration

open System.Text
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
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
        | ExitCode.Failed -> "A known failure; check any earlier unconfirmed change separately."
        | ExitCode.OutcomeUnknown ->
            "An operation lookup is pending, or commit may have happened; retain an uncertain mutation's exact frame for recovery."
        | ExitCode.Diverged -> "Ledger verification found divergence."
        | ExitCode.Usage -> "CLI command usage was refused before dispatch."
        | ExitCode.InternalError -> "Unexpected CLI defect."
        | ExitCode.Cancelled -> "CLI interrupted or cancelled."

    let source () =
        let english = CatalogValidation.validate () |> Map.find "en"

        let message key =
            let arguments =
                if key = "endpoint.operation.get.meaning" then
                    let duration =
                        Template.render
                            english["label.operationWindow"]
                            (Map.ofList
                                [
                                    "minutes",
                                    string (OperationLifetime.WindowMilliseconds / 60000L)
                                ])

                    Map.ofList [ "window", duration ]
                else
                    Map.empty

            Template.render english[key] arguments

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
            "Generated from the Wire catalog and CLI grammar. Regenerate with `dotnet run --project eng/ClaimCore.ContractGenerator -- --output contracts`; `--check` verifies this file."
            ""
            "Required inputs are unbracketed; optional inputs are bracketed. Choices use lowercase kebab spelling; exact uppercase wire tokens are also accepted. Value-taking flags accept `--name=value`; switches are valueless."
            ""
            "[CLI use](integrating.md#cli-use) defines framing, configuration, limits, review and export behavior. [Delivery recovery](integrating.md#delivery-recovery) defines UUIDv7 lifetime, exact retries and final lookup."
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
            "Both executables use the shared Presentation exit-code table. `claimcore-database help` reports its owner-command grammar without opening credentials or the database. Database refusals include a diagnostic; failures include a fault. Usage errors include the applicable synopsis."
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
