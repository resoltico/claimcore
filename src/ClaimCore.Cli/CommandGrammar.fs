namespace ClaimCore.Cli

open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation
open System
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
type CatalogArgument =
    | Named
    | Operand
    | Implicit

[<RequireQualifiedAccess>]
type CatalogAtom =
    | Text
    | Integer
    | Boolean
    | Choice of string list
    | Choices of string list

type CommandInput =
    {
        Name: TaskInputId
        Atom: CatalogAtom
        Argument: CatalogArgument
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

module CommandGrammar =
    let operations = OperationCatalog.all

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
        | TaskInputId.ReminderState -> "state"
        | TaskInputId.Target InputTarget.ReminderAction -> "action"
        | TaskInputId.Target InputTarget.ReminderDueDate -> "due"
        | TaskInputId.TimelineFilter -> "filter"
        | input -> TaskInputId.key input

    let words (entry: CatalogOperation) =
        match entry.SelectedAct with
        | Some(ActKind.Case kind) -> [ "case"; kebab (CaseActKind.token kind) ]
        | Some(ActKind.Note NoteActKind.AddNote) -> [ "note"; "add" ]
        | Some(ActKind.Note NoteActKind.RemoveNote) -> [ "note"; "remove" ]
        | Some(ActKind.Work WorkActKind.UpdateWork) -> [ "work"; "update" ]
        | Some(ActKind.Reminder kind) -> [ "reminder"; kebab (ReminderActKind.token kind) ]
        | Some(ActKind.Authority kind) -> [ "authority"; kebab (AuthorityKind.token kind) ]
        | None when entry.Endpoint = ActorEndpoint.Query QueryTaskId.CasePayments ->
            [ "payment"; "list" ]
        | None when entry.Endpoint = ActorEndpoint.Query QueryTaskId.CaseNotes -> [ "note"; "list" ]
        | None -> (ActorEndpoint.token entry.Endpoint).Split('.') |> List.ofArray

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

    let task (operation: CatalogOperation) =
        match operation.SelectedAct, operation.Endpoint with
        | Some act, _ -> TaskId.Operation act
        | None, ActorEndpoint.Query query -> TaskId.Query query
        | _ -> invalidOp "A command requires a semantic task."

    let inputs operation =
        (TaskDeclarations.describe (task operation)).Inputs
        |> List.map (fun declaration ->
            let name = declaration.Input

            let required =
                declaration.Mode = TaskInputMode.Required
                && (match name, operation.SelectedAct with
                    | TaskInputId.Target(InputTarget.Group _), _ -> false
                    | _, Some(ActKind.Case kind) when
                        declaration.Source = InputSource.ExpectedRevision
                        && (CaseActKind.describe kind).Revision = RevisionInput.New
                        ->
                        false
                    | _ -> true)

            let argument =
                match name with
                | TaskInputId.Scope -> CatalogArgument.Implicit
                | TaskInputId.Target(InputTarget.Field BusinessField.CaseReference) when required ->
                    CatalogArgument.Operand
                | TaskInputId.Target InputTarget.OperationId when
                    declaration.Source = InputSource.Authored
                    ->
                    CatalogArgument.Operand
                | TaskInputId.ActorId when required -> CatalogArgument.Operand
                | _ -> CatalogArgument.Named

            let atom =
                match TaskChoiceId.ofInput name with
                | Some choice ->
                    match name with
                    | TaskInputId.Lifecycle
                    | TaskInputId.Status
                    | TaskInputId.Progress -> CatalogAtom.Choices(TaskChoiceId.tokens choice)
                    | _ -> CatalogAtom.Choice(TaskChoiceId.tokens choice)
                | None when
                    declaration.Source = InputSource.ExpectedRevision
                    || name = TaskInputId.Target InputTarget.Limit
                    ->
                    CatalogAtom.Integer
                | None when name = TaskInputId.IncludeInactive -> CatalogAtom.Boolean
                | None -> CatalogAtom.Text

            {
                Name = name
                Atom = atom
                Argument = argument
                Declaration = declaration
                Required = required
                LabelId = TaskInputPresentation.messageKey name "label"
            })

    let collections operation =
        (TaskDeclarations.describe (task operation)).Collections
        |> List.map (fun item ->
            match item.Collection with
            | TaskCollectionId.AuthorityGrants ->
                {
                    Collection = item.Collection
                    Name = TaskInputId.Target InputTarget.Grants
                    LabelId =
                        TaskInputPresentation.messageKey
                            (TaskInputId.Target InputTarget.Grants)
                            "label"
                    MinimumItems = AuthorityAdmission.MinimumGrants
                    MaximumItems = AuthorityAdmission.MaximumGrants
                })

    let collectionFlag (definition: CommandCollection) =
        match definition.Collection with
        | TaskCollectionId.AuthorityGrants ->
            "--" + kebab (inputName (TaskInputId.Target InputTarget.Role))

    let collectionPlaceholder (definition: CommandCollection) =
        match definition.Collection with
        | TaskCollectionId.AuthorityGrants -> "ROLE[@CASE-REF]"

    let families = operations |> List.map (words >> List.head) |> List.distinct

    let supportsAll (operation: CatalogOperation) =
        List.contains
            operation.Endpoint
            [
                ActorEndpoint.Query QueryTaskId.CasePayments
                ActorEndpoint.Query QueryTaskId.CaseNotes
                ActorEndpoint.Query QueryTaskId.CaseList
                ActorEndpoint.Query QueryTaskId.ReminderList
                ActorEndpoint.Query QueryTaskId.CaseTimeline
            ]

    let formats (operation: CatalogOperation) =
        if
            List.contains
                operation.Endpoint
                [
                    ActorEndpoint.Query QueryTaskId.CaseList
                    ActorEndpoint.Query QueryTaskId.ReminderList
                    ActorEndpoint.Query QueryTaskId.AuthorityHistory
                    ActorEndpoint.Query QueryTaskId.CaseTimeline
                ]
        then
            [ "text"; "json"; "csv" ]
        else
            [ "text"; "json" ]

    let globalOptions operation =
        [
            "--format", String.concat "|" (formats operation)
            "--language", String.concat "|" LocalizationPolicy.languages
            "--dry-run", ""
            "--details", ""
            if CatalogOperation.consequence operation = Consequence.Irreversible then
                "--confirm", "TARGET"
            if operation.SelectedAct.IsSome then
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

    /// Clears describe replacement intent, never optional values on a creation.
    let canClear (operation: CatalogOperation) (input: CommandInput) =
        match input.Name, operation.SelectedAct with
        | TaskInputId.Target(InputTarget.Field field),
          Some(ActKind.Case(CaseActKind.AmendRegistration | CaseActKind.Correct)) ->
            (BusinessField.describe field).AllowsAbsence
        | TaskInputId.Target(InputTarget.Field BusinessField.HandlingSummary), Some(ActKind.Work _) ->
            true
        | TaskInputId.Target InputTarget.ProfileContact,
          Some(ActKind.Authority AuthorityKind.SetProfile) -> true
        | _ -> false

    let synopsis (operation: CatalogOperation) =
        let placeholder =
            function
            | CatalogAtom.Text -> "VALUE"
            | CatalogAtom.Integer -> "N"
            | CatalogAtom.Boolean -> "true|false"
            | CatalogAtom.Choice choices -> choices |> List.map kebab |> String.concat "|"
            | CatalogAtom.Choices choices -> choices |> List.map kebab |> String.concat "|"

        let declarations = inputs operation

        let arguments =
            declarations
            |> List.choose (fun input ->
                match input.Argument with
                | CatalogArgument.Implicit -> None
                | CatalogArgument.Operand ->
                    Some(kebab (inputName input.Name) |> _.ToUpperInvariant())
                | CatalogArgument.Named ->
                    let suffix = placeholder input.Atom

                    let text =
                        "--"
                        + kebab (inputName input.Name)
                        + (if suffix = "" then "" else " " + suffix)

                    let syntax = if input.Required then text else "[" + text + "]"

                    Some(
                        match input.Atom with
                        | CatalogAtom.Choices _ -> syntax + "..."
                        | _ -> syntax
                    ))


        let textSources =
            plainTextInputs operation
            |> List.map (fun (input, _) ->
                let flag = "--" + kebab (inputName input.Name)
                "[" + flag + "-file PATH | " + flag + "-stdin | " + flag + "-editor]")

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
                "[" + flag + (if value = "" then "" else " " + value) + "]")

        let repeated =
            collections operation
            |> List.map (fun definition ->
                let syntax = collectionFlag definition + " " + collectionPlaceholder definition

                (if definition.MinimumItems > 0 then
                     syntax
                 else
                     "[" + syntax + "]")
                + "...")

        String.concat " " (words operation @ arguments @ repeated @ textSources @ clears @ globals)
