namespace ClaimCore.Cli

open System
open System.Collections.Generic
open System.Globalization
open ClaimCore.CommandLine
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Json

/// Argv acquires declared tokens; Core assembles and validates their meaning.
module internal TaskArguments =
    let private flag input =
        "--" + CommandGrammar.kebab (CommandGrammar.inputName input)

    let private target input = TaskInputId.Target input

    let mutation
        incomplete
        allocate
        kind
        (options: Dictionary<string, string option list>)
        operands
        (problems: ResizeArray<UsageError>)
        =
        let task = TaskId.Operation kind
        let declarations = CommandGrammar.inputs task
        let mutable remaining = operands

        let raw input =
            match options.TryGetValue(flag input) with
            | true, values -> values |> List.tryHead |> Option.flatten
            | _ -> None

        let acquire input =
            match input.Argument, remaining with
            | CommandArgument.Operand, value :: rest ->
                remaining <- rest
                Some value
            | CommandArgument.Operand, [] ->
                problems.Add(
                    UsageError.MissingOperand(
                        CommandGrammar.inputName input.Name |> _.ToUpperInvariant()
                    )
                )

                None
            | _ -> raw input.Name

        let normalize (input: CommandInput) value =
            match input.Atom with
            | CommandAtom.Choice choices ->
                choices
                |> List.tryFind (fun token -> value = token || value = CommandGrammar.kebab token)
                |> Option.defaultWith (fun () ->
                    problems.Add(
                        UsageError.InvalidChoice(
                            flag input.Name,
                            value,
                            choices |> List.map CommandGrammar.kebab
                        )
                    )

                    value)
            | _ -> value

        let values =
            declarations
            |> List.choose (fun input ->
                let acquired = acquire input

                let clear =
                    options.ContainsKey(
                        "--clear-" + CommandGrammar.kebab (CommandGrammar.inputName input.Name)
                    )

                if clear && acquired.IsSome then
                    problems.Add(UsageError.DuplicateOption(flag input.Name))

                let acquired =
                    match acquired, input.Name with
                    | None, TaskInputId.Target(InputTarget.Group _) -> Some "KEEP"
                    | _ -> acquired

                if
                    acquired.IsNone
                    && input.Required
                    && input.Argument <> CommandArgument.Implicit
                    && not clear
                    && not (incomplete && input.Declaration.Source = InputSource.ExpectedRevision)
                then
                    problems.Add(UsageError.MissingOption(flag input.Name))

                if clear then
                    Some(input.Name, SubmissionValue.Clear)
                else
                    acquired
                    |> Option.map (fun raw ->
                        let value = normalize input raw

                        let scalar =
                            match input.Declaration.Atom with
                            | TaskAtom.Integer ->
                                match
                                    Int64.TryParse(
                                        value,
                                        NumberStyles.AllowLeadingSign,
                                        CultureInfo.InvariantCulture
                                    )
                                with
                                | true, number when
                                    number
                                    >= (if
                                            input.Name =
                                                target InputTarget.ExpectedReminderRevision
                                        then
                                            1L
                                        else
                                            0L)
                                    && number <= Revision.Maximum
                                    ->
                                    SubmissionValue.Integer number
                                | _ ->
                                    problems.Add(UsageError.InvalidValue(flag input.Name, value))
                                    SubmissionValue.Integer 0L
                            | TaskAtom.Boolean ->
                                match Boolean.TryParse value with
                                | true, value -> SubmissionValue.Boolean value
                                | _ ->
                                    problems.Add(UsageError.InvalidValue(flag input.Name, value))
                                    SubmissionValue.Boolean false
                            | TaskAtom.Text -> SubmissionValue.Text value

                        input.Name, scalar))
            |> Map.ofList

        remaining |> List.iter (UsageError.ExtraOperand >> problems.Add)

        let text input =
            match Map.tryFind input values with
            | Some(SubmissionValue.Text value) -> Some value
            | _ -> None

        let uuid input =
            text input
            |> Option.bind (fun value ->
                match Guid.TryParseExact(value, "D") with
                | true, id -> Some id
                | _ ->
                    problems.Add(UsageError.InvalidValue(flag input, value))
                    None)

        let revision input =
            match Map.tryFind (target input) values with
            | Some(SubmissionValue.Integer value) -> Some value
            | _ -> None

        let selection () =
            text (target (InputTarget.Field BusinessField.CaseReference))
            |> Option.defaultValue ""
            |> CaseSelector.Reference

        let binding =
            match ActKind.subject kind with
            | ActSubject.Case ->
                let initial =
                    match kind with
                    | ActKind.Case kind -> (CaseActKind.describe kind).Revision = RevisionInput.New
                    | _ -> false

                OperationBinding.Case(
                    selection (),
                    (if initial then
                         Some 0L
                     else
                         revision InputTarget.ExpectedRevision),
                    uuid (target InputTarget.PaymentId)
                )
            | ActSubject.Work ->
                OperationBinding.Work(selection (), revision InputTarget.ExpectedWorkRevision)
            | ActSubject.Note ->
                OperationBinding.Note(selection (), uuid (target InputTarget.NoteId))
            | ActSubject.Reminder ->
                OperationBinding.Reminder(
                    selection (),
                    uuid (target InputTarget.ReminderId)
                    |> Option.map (fun id -> id, revision InputTarget.ExpectedReminderRevision)
                )
            | ActSubject.People -> OperationBinding.People(uuid TaskInputId.ActorId)

        let authored =
            declarations
            |> List.filter (fun input -> input.Declaration.Source = InputSource.Authored)
            |> List.choose (fun input ->
                Map.tryFind input.Name values |> Option.map (fun value -> input.Name, value))
            |> Map.ofList

        let authored =
            if authored.ContainsKey TaskInputId.Scope then
                authored
            elif
                (TaskDeclarations.describe task).Inputs
                |> List.exists (fun input -> input.Input = TaskInputId.Scope)
            then
                authored
                |> Map.add
                    TaskInputId.Scope
                    (SubmissionValue.Text(
                        if
                            authored.ContainsKey(
                                target (InputTarget.Field BusinessField.CaseReference)
                            )
                        then
                            "CASE"
                        else
                            "INSTALLATION"
                    ))
            else
                authored

        let authored =
            CommandGrammar.collections task
            |> List.fold
                (fun values collection ->
                    let name = CommandGrammar.collectionFlag collection

                    let roles =
                        match options.TryGetValue name with
                        | true, values -> values |> List.rev |> List.choose id
                        | _ -> []

                    if roles.Length < collection.MinimumItems then
                        problems.Add(UsageError.MissingOption name)

                    let rows =
                        roles
                        |> List.map (fun value ->
                            let split = value.Split([| '@' |], 2)

                            let role =
                                Role.all
                                |> List.tryFind (fun role ->
                                    split[0] = Role.token role
                                    || split[0] = CommandGrammar.kebab (Role.token role))

                            let token =
                                role
                                |> Option.map Role.token
                                |> Option.defaultWith (fun () ->
                                    problems.Add(
                                        UsageError.InvalidChoice(
                                            name,
                                            value,
                                            Role.all |> List.map Role.token
                                        )
                                    )

                                    split[0])

                            [
                                target InputTarget.Role, SubmissionValue.Text token
                                TaskInputId.Scope,
                                SubmissionValue.Text(
                                    if split.Length = 2 then "CASE" else "INSTALLATION"
                                )
                                if split.Length = 2 then
                                    target (InputTarget.Field BusinessField.CaseReference),
                                    SubmissionValue.Text split[1]
                            ]
                            |> Map.ofList)

                    values |> Map.add collection.Name (SubmissionValue.Rows rows))
                authored

        let operation =
            match uuid (target InputTarget.OperationId) with
            | Some id -> Some id
            | None when incomplete -> None
            | None ->
                match Guid.TryParseExact(allocate (), "D") with
                | true, id -> Some id
                | _ -> invalidOp "Operation allocation must return a UUID."

        {
            SubmissionEnvelope.OperationId = operation
            Task = kind
            Binding = binding
            Inputs = authored
        }

    let rec private scalar =
        function
        | SubmissionValue.Text value -> JsonValue.String value
        | SubmissionValue.Integer value -> JsonValue.Integer value
        | SubmissionValue.Boolean value -> JsonValue.Boolean value
        | SubmissionValue.Clear -> JsonValue.Null
        | SubmissionValue.Rows rows -> JsonValue.Array(rows |> List.map inputs)

    and private inputs values =
        values
        |> Map.toList
        |> List.map (fun (input, value) -> TaskInputId.token input, scalar value)
        |> JsonValue.Object

    /// Offline encodings omit metadata that has not been acquired.
    let write (draft: SubmissionEnvelope<int64 option, Guid option>) =
        let selected target =
            "target", RequestCodecs.caseSelector.Write target

        let version revision =
            revision
            |> Option.map (fun value -> "expectedRevision", JsonValue.Integer value)
            |> Option.toList

        let id name value =
            value
            |> Option.map (fun (value: Guid) -> name, JsonValue.String(value.ToString("D")))
            |> Option.toList

        let binding =
            match draft.Binding with
            | OperationBinding.Case(target, revision, payment) ->
                selected target :: (version revision @ id "paymentId" payment)
            | OperationBinding.Work(target, revision) -> selected target :: version revision
            | OperationBinding.Note(target, note) -> selected target :: id "noteId" note
            | OperationBinding.Reminder(target, current) ->
                selected target
                :: (current
                    |> Option.map (fun (reminder, revision) ->
                        id "reminderId" (Some reminder) @ version revision)
                    |> Option.defaultValue [])
            | OperationBinding.People actor -> id "actorId" actor

        JsonValue.Object(
            id "operationId" draft.OperationId
            @ [
                "task", JsonValue.String(ActKind.token draft.Task)
                "binding",
                JsonValue.Object(
                    ("kind", JsonValue.String(ActSubject.token (ActKind.subject draft.Task)))
                    :: binding
                )
                "inputs", inputs draft.Inputs
            ]
        )
