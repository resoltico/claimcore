namespace ClaimCore.Cli

open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open System
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type CommandArgument =
    | Named
    | Operand
    | Implicit

[<RequireQualifiedAccess>]
type CommandAtom =
    | Text
    | Integer
    | Boolean
    | Choice of string list
    | Choices of string list

type CommandInput =
    {
        Name: TaskInputId
        Atom: CommandAtom
        Argument: CommandArgument
        Declaration: TaskInputDeclaration
        Required: bool
        LabelId: string
    }

type CommandCollection =
    {
        Collection: TaskCollectionId
        Name: TaskInputId
        LabelId: string
        MinimumItems: int
        MaximumItems: int
    }

type CommandOptionGrammar =
    {
        Known: string list
        Available: string list
        Switches: string list
        Repeated: string list
    }

module CommandGrammar =
    let operations = (TaskDeclarations.all |> List.map _.Id)

    let kebab (value: string) =
        Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2").Replace('_', '-').ToLowerInvariant()

    // This flat input-name table has no nested decisions; canonical flag names have one owner.
    let inputName =
        function
        | TaskInputId.Target InputTarget.NoteText -> "text"
        | TaskInputId.Target InputTarget.DirectorySearch -> "text"
        | TaskInputId.Target InputTarget.PrincipalKind -> "principalKind"
        | TaskInputId.Target InputTarget.PrincipalIssuer -> "issuer"
        | TaskInputId.Target InputTarget.PrincipalSubject -> "subject"
        | TaskInputId.Target InputTarget.ProfileDisplayName -> "name"
        | TaskInputId.Target InputTarget.ProfileContact -> "contact"
        | TaskInputId.CaseView
        | TaskInputId.ReminderView -> "view"
        | TaskInputId.ResponsibleActorFilter -> "responsibleActor"
        | TaskInputId.RoleFilter -> "role"
        | TaskInputId.ReminderDue -> "due"
        | TaskInputId.DeadlineOutcomeFilter -> "deadline-outcome"
        | TaskInputId.ReminderKindFilter
        | TaskInputId.Target InputTarget.ReminderPlanKind -> "kind"
        | TaskInputId.Target InputTarget.ReminderDoneOn -> "done-on"
        | TaskInputId.ReminderState -> "state"
        | TaskInputId.Target InputTarget.ReminderAction -> "action"
        | TaskInputId.Target InputTarget.ReminderDueDate -> "due"
        | TaskInputId.TimelineFilter -> "filter"
        | input -> TaskInputId.key input

    let words (entry: string) =
        entry.Split('.') |> Array.map kebab |> List.ofArray

    /// Prefer the complete command when one command is also a prefix of another.
    let tryOperation arguments =
        operations
        |> List.sortByDescending (words >> List.length)
        |> List.tryFind (fun operation ->
            let path = words operation
            List.truncate path.Length arguments = path)

    /// One argument spelling works in the interactive shell and the documented POSIX examples.
    let quoteArgument (value: string) =
        if Regex.IsMatch(value, "^[A-Za-z0-9._-]+$") then
            value
        else
            "'" + value.Replace("'", "'\\''") + "'"

    let commandText operation =
        CliIdentity.programName + " " + String.concat " " (words operation)

    let private declaredInputs operation =
        (TaskDeclarations.describe operation).Inputs
        |> List.map (fun declaration ->
            let name = declaration.Input

            let fixedRevision =
                declaration.Source = InputSource.ExpectedRevision
                && TaskRouting.creates operation

            let required =
                declaration.Mode = TaskInputMode.Required
                && not (
                    declaration.Source = InputSource.OperationIdentity
                    && (TaskRouting.act operation).IsSome
                )
                && not fixedRevision
                && (match name with
                    | TaskInputId.Target(InputTarget.Group _) -> false
                    | _ -> true)

            let argument =
                match name with
                | _ when fixedRevision -> CommandArgument.Implicit
                | TaskInputId.Scope -> CommandArgument.Implicit
                | TaskInputId.Target(InputTarget.Field BusinessField.CaseReference) when required ->
                    CommandArgument.Operand
                | TaskInputId.Target InputTarget.OperationId when
                    declaration.Source = InputSource.Authored
                    ->
                    CommandArgument.Operand
                | TaskInputId.ActorId when required -> CommandArgument.Operand
                | _ -> CommandArgument.Named

            let atom =
                match TaskChoiceId.ofInput name with
                | Some choice ->
                    if declaration.Multiple then
                        CommandAtom.Choices(TaskChoiceId.tokens choice)
                    else
                        CommandAtom.Choice(TaskChoiceId.tokens choice)
                | None when
                    declaration.Source = InputSource.ExpectedRevision
                    || name = TaskInputId.Target InputTarget.Limit
                    ->
                    CommandAtom.Integer
                | None when name = TaskInputId.IncludeInactive -> CommandAtom.Boolean
                | None -> CommandAtom.Text

            {
                Name = name
                Atom = atom
                Argument = argument
                Declaration = declaration
                Required = required
                LabelId = TaskInputPresentation.messageKey name "label"
            })

    /// ID selection is an argv alternative to an existing-case reference, never a new case reference.
    let selectsExistingCase operation =
        let declaration = TaskDeclarations.describe operation

        declaration.Inputs
        |> List.exists (fun input ->
            input.Input = TaskInputId.Target(InputTarget.Field BusinessField.CaseReference)
            && (input.Source = InputSource.Binding || declaration.Kind = ClientTaskKind.Query))

    let inputs operation =
        let inputs = declaredInputs operation

        if
            selectsExistingCase operation
            && not (inputs |> List.exists (fun input -> input.Name = TaskInputId.CaseId))
        then
            let declaration =
                TaskInputDeclaration.create TaskInputMode.Optional TaskInputId.CaseId

            inputs
            @ [
                {
                    Name = TaskInputId.CaseId
                    Atom = CommandAtom.Text
                    Argument = CommandArgument.Named
                    Declaration =
                        { declaration with
                            Source = InputSource.Binding
                        }
                    Required = false
                    LabelId = TaskInputPresentation.messageKey TaskInputId.CaseId "label"
                }
            ]
        else
            inputs

    let collections operation =
        (TaskDeclarations.describe operation).Collections
        |> List.map (fun item ->
            let input = TaskCollectionId.input item.Collection

            {
                Collection = item.Collection
                Name = input
                LabelId = TaskInputPresentation.messageKey input "label"
                MinimumItems = item.MinimumRows
                MaximumItems = TaskCollectionId.maximumRows item.Collection
            })

    let collectionFlag (definition: CommandCollection) =
        match definition.Collection with
        | TaskCollectionId.Parties -> "--party"
        | TaskCollectionId.PeopleGrants ->
            "--" + kebab (inputName (TaskInputId.Target InputTarget.Role))

    let collectionPlaceholder (definition: CommandCollection) =
        match definition.Collection with
        | TaskCollectionId.PeopleGrants -> "ROLE[@CASE-REF]"
        | TaskCollectionId.Parties -> "JSON"

    let families = operations |> List.map (words >> List.head) |> List.distinct

    let supportsAll operation =
        EndpointDeclarations.find operation |> Option.exists _.Paged

    let formats operation =
        if EndpointDeclarations.find operation |> Option.exists _.Csv then
            [ "text"; "json"; "csv" ]
        else
            [ "text"; "json" ]

    let globalOptions operation =
        [
            "--format", String.concat "|" (formats operation)
            "--language", String.concat "|" LocalizationPolicy.languages
            "--display-locale", String.concat "|" LocalizationPolicy.displayLocales
            "--dry-run", ""
            "--details", ""
            if TaskRouting.consequence operation = Consequence.Irreversible then
                "--confirm", "TARGET"
            if (TaskRouting.act operation).IsSome then
                "--preview", ""
            if supportsAll operation then
                "--all", ""
        ]

    let globalFlags =
        operations |> List.collect (globalOptions >> List.map fst) |> List.distinct

    let globalSwitches =
        operations
        |> List.collect (
            globalOptions
            >> List.choose (fun (flag, value) -> if value = "" then Some flag else None)
        )
        |> List.distinct

    let plainTextInputs operation =
        inputs operation
        |> List.choose (fun input ->
            match TaskInputPresentation.scalar input.Name with
            | Some(ScalarRule.PlainText constraints) -> Some(input, constraints)
            | _ -> None)

    let canClear (_operation: string) (input: CommandInput) = input.Declaration.Clearable

    /// Source acquisition and option lexing share the same interface-owned arity.
    let textSourceSuffixes = [ "-file"; "-stdin"; "-editor" ]
    let sourceHasOperand suffix = suffix = "-file"

    /// The outer executable sees source switches before text acquisition expands them.
    let textSourceSwitches operation =
        plainTextInputs operation
        |> List.collect (fun (input, _) ->
            textSourceSuffixes
            |> List.filter (sourceHasOperand >> not)
            |> List.map (fun suffix -> "--" + kebab (inputName input.Name) + suffix))

    let optionGrammar operation =
        let flag input = "--" + kebab (inputName input)

        let named =
            inputs operation
            |> List.filter (fun input -> input.Argument = CommandArgument.Named)

        let clears =
            named
            |> List.choose (fun input ->
                if canClear operation input then
                    Some("--clear-" + kebab (inputName input.Name))
                else
                    None)

        let rows = collections operation |> List.map collectionFlag
        let flags = (named |> List.map (fun input -> flag input.Name)) @ clears @ rows

        {
            Known = globalFlags @ flags
            Available = (globalOptions operation |> List.map fst) @ flags
            Switches = globalSwitches @ clears
            Repeated =
                (named
                 |> List.choose (fun input ->
                     match input.Atom with
                     | CommandAtom.Choices _ -> Some(flag input.Name)
                     | _ -> None))
                @ rows
        }

    let synopsisForDraft draft (operation: string) =
        let placeholder =
            function
            | CommandAtom.Text -> "VALUE"
            | CommandAtom.Integer -> "N"
            | CommandAtom.Boolean -> "true|false"
            | CommandAtom.Choice choices -> choices |> List.map kebab |> String.concat "|"
            | CommandAtom.Choices choices -> choices |> List.map kebab |> String.concat "|"

        let declarations = inputs operation

        let positional =
            declarations
            |> List.filter (fun input -> input.Argument = CommandArgument.Operand)

        let positionalCaseAlternative =
            match positional with
            | [ input ] ->
                input.Name = TaskInputId.Target(InputTarget.Field BusinessField.CaseReference)
                && selectsExistingCase operation
            | _ -> false

        let arguments =
            declarations
            |> List.filter (fun input ->
                not (input.Name = TaskInputId.CaseId && positionalCaseAlternative))
            |> List.choose (fun input ->
                match input.Argument with
                | CommandArgument.Implicit
                | CommandArgument.Operand -> None
                | CommandArgument.Named ->
                    let suffix = placeholder input.Atom

                    let text =
                        "--"
                        + kebab (inputName input.Name)
                        + (if suffix = "" then "" else " " + suffix)

                    let required =
                        input.Required
                        && not (draft && input.Declaration.Source = InputSource.ExpectedRevision)

                    let syntax = if required then text else "[" + text + "]"

                    Some(
                        match input.Atom with
                        | CommandAtom.Choices _ -> syntax + "..."
                        | _ -> syntax
                    ))


        let textSources =
            plainTextInputs operation
            |> List.map (fun (input, _) ->
                let flag = "--" + kebab (inputName input.Name)

                textSourceSuffixes
                |> List.map (fun suffix ->
                    flag + suffix + (if sourceHasOperand suffix then " PATH" else ""))
                |> String.concat " | "
                |> fun choices -> "[" + choices + "]")

        let clears =
            declarations
            |> List.choose (fun input ->
                if canClear operation input then
                    Some("[--clear-" + kebab (inputName input.Name) + "]")
                else
                    None)

        let globals =
            globalOptions operation
            |> List.map (fun (flag, value) ->
                let text = flag + (if value = "" then "" else " " + value)

                if flag = "--confirm" && not draft then
                    text
                else
                    "[" + text + "]")

        let repeated =
            collections operation
            |> List.map (fun definition ->
                let syntax = collectionFlag definition + " " + collectionPlaceholder definition

                (if definition.MinimumItems > 0 then
                     syntax
                 else
                     "[" + syntax + "]")
                + "...")

        let operands =
            positional
            |> List.map (fun input -> kebab (inputName input.Name) |> _.ToUpperInvariant())

        let operands =
            match operands with
            | [] -> []
            | [ _ ] when positionalCaseAlternative -> [ "(--case-id UUID | [--] CASE-REFERENCE)" ]
            | values -> "[--]" :: values

        String.concat
            " "
            (words operation
             @ arguments
             @ repeated
             @ textSources
             @ clears
             @ globals
             @ operands)

    let synopsis operation = synopsisForDraft false operation
