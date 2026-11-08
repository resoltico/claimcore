namespace ClaimCore.Cli

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

type CatalogDerivedInput =
    {
        Name: InteractionInput
        Atom: CatalogAtom
        Argument: CatalogArgument
        Binding: CatalogInput
    }

module CommandGrammar =
    let operations = OperationCatalog.all

    let kebab (value: string) =
        Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2").Replace('_', '-').ToLowerInvariant()

    // This flat input-name table has no nested decisions; canonical flag names have one owner.
    let inputName =
        function
        | InteractionInput.Target InputTarget.DirectorySearch -> "text"
        | InteractionInput.Target InputTarget.PrincipalKind -> "principalKind"
        | InteractionInput.Target InputTarget.PrincipalIssuer -> "issuer"
        | InteractionInput.Target InputTarget.PrincipalSubject -> "subject"
        | InteractionInput.Target InputTarget.ProfileDisplayName -> "name"
        | InteractionInput.Target InputTarget.ProfileContact -> "contact"
        | InteractionInput.CaseView
        | InteractionInput.ReminderView -> "view"
        | InteractionInput.ResponsibleActorFilter -> "responsibleActor"
        | InteractionInput.RoleFilter -> "role"
        | InteractionInput.ReminderDue -> "due"
        | InteractionInput.ReminderState -> "state"
        | InteractionInput.Target InputTarget.ReminderAction -> "action"
        | InteractionInput.Target InputTarget.ReminderDueDate -> "due"
        | InteractionInput.TimelineFilter -> "filter"
        | input -> InteractionInput.key input

    let words (entry: CatalogOperation) =
        match entry.SelectedAct with
        | Some(ActKind.Case kind) -> [ "case"; kebab (CaseActKind.token kind) ]
        | Some(ActKind.Work WorkActKind.UpdateWork) -> [ "work"; "update" ]
        | Some(ActKind.Reminder kind) -> [ "reminder"; kebab (ReminderActKind.token kind) ]
        | Some(ActKind.Authority kind) -> [ "authority"; kebab (AuthorityKind.token kind) ]
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

    let derive (operation: CatalogOperation) =
        let bindings = OperationCatalog.derive operation
        let inputs, constants = bindings.Inputs, bindings.Constants

        let grammar (input: OperationInputDefinition) =
            let argument =
                match input.Name with
                | InteractionInput.Scope -> CatalogArgument.Implicit
                | InteractionInput.Target(InputTarget.Field BusinessField.CaseReference) when
                    input.Binding.Path = [ "target"; "caseReference" ] && input.Binding.Required
                    ->
                    CatalogArgument.Operand
                | (InteractionInput.Target InputTarget.OperationId) when
                    input.Binding.Source <> InputSource.OperationIdentity
                    ->
                    CatalogArgument.Operand
                | InteractionInput.ActorId when input.Binding.Required -> CatalogArgument.Operand
                | _ -> CatalogArgument.Named

            let atom =
                match input.Name, input.Binding.Kind with
                | _, BindingKind.Text -> CatalogAtom.Text
                | _, BindingKind.Integer -> CatalogAtom.Integer
                | _, BindingKind.Boolean -> CatalogAtom.Boolean
                | _, BindingKind.Choices(_, choices) -> CatalogAtom.Choices choices
                | _, BindingKind.Choice(_, choices)
                | _, BindingKind.Group(_, choices) -> CatalogAtom.Choice choices

            {
                Name = input.Name
                Atom = atom
                Argument = argument
                Binding = input.Binding
            }

        inputs |> List.map grammar, constants

    let collections operation =
        (OperationCatalog.derive operation).Collections

    let collectionFlag (definition: OperationCollectionDefinition) =
        match definition.Name with
        | InteractionInput.Target InputTarget.Grants ->
            "--" + kebab (inputName (InteractionInput.Target InputTarget.Role))
        | _ -> invalidOp "A declared collection requires an explicit CLI argument syntax."

    let collectionPlaceholder (definition: OperationCollectionDefinition) =
        match definition.Name with
        | InteractionInput.Target InputTarget.Grants -> "ROLE[@CASE-REF]"
        | _ -> invalidOp "A declared collection requires an explicit CLI argument syntax."

    let families = operations |> List.map (words >> List.head) |> List.distinct

    let supportsAll (operation: CatalogOperation) =
        List.contains
            operation.Endpoint
            [
                ActorEndpoint.Query QueryEndpoint.CaseList
                ActorEndpoint.Query QueryEndpoint.ReminderList
                ActorEndpoint.Query QueryEndpoint.CaseTimeline
            ]

    let formats (operation: CatalogOperation) =
        if
            List.contains
                operation.Endpoint
                [
                    ActorEndpoint.Query QueryEndpoint.CaseList
                    ActorEndpoint.Query QueryEndpoint.ReminderList
                    ActorEndpoint.Query QueryEndpoint.AuthorityHistory
                    ActorEndpoint.Query QueryEndpoint.CaseTimeline
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
        let scalar name =
            match name with
            | InteractionInput.Target(InputTarget.Field field) ->
                Some(FieldPresentation.descriptor field)
            | _ ->
                Catalog.document.InputFields
                |> List.tryFind (fun field -> field.Name = InteractionInput.key name)
                |> Option.bind _.Scalar

        derive operation
        |> fst
        |> List.choose (fun input ->
            match scalar input.Name with
            | Some(ScalarRule.PlainText constraints) -> Some(input, constraints)
            | _ -> None)

    /// Clears describe replacement intent, never optional values on a creation.
    let canClear (operation: CatalogOperation) (input: CatalogDerivedInput) =
        input.Binding.AllowsNull
        && match operation.SelectedAct with
           | Some(ActKind.Case kind) when (CaseActKind.describe kind).Revision = RevisionInput.New ->
               false
           | Some(ActKind.Authority AuthorityKind.Register)
           | Some(ActKind.Authority AuthorityKind.Admit)
           | Some(ActKind.Reminder ReminderActKind.Create) -> false
           | _ -> true

    let synopsis (operation: CatalogOperation) =
        let placeholder =
            function
            | CatalogAtom.Text -> "VALUE"
            | CatalogAtom.Integer -> "N"
            | CatalogAtom.Boolean -> "true|false"
            | CatalogAtom.Choice choices -> choices |> List.map kebab |> String.concat "|"
            | CatalogAtom.Choices choices -> choices |> List.map kebab |> String.concat "|"

        let inputs, _ = derive operation

        let arguments =
            inputs
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

                    let syntax =
                        if input.Binding.Required && input.Binding.Branches.IsEmpty then
                            text
                        else
                            "[" + text + "]"

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
            inputs
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

                (if definition.Binding.Required then
                     syntax
                 else
                     "[" + syntax + "]")
                + "...")

        String.concat " " (words operation @ arguments @ repeated @ textSources @ clears @ globals)
