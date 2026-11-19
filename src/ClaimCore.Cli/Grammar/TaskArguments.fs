namespace ClaimCore.Cli

open System
open System.Collections.Generic
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
        (source: AttachmentSource option)
        (options: Dictionary<string, string option list>)
        operands
        (problems: ResizeArray<UsageError>)
        =
        let task = TaskId.token kind
        let declarations = CommandGrammar.inputs task
        let mutable remaining = operands

        let raw input =
            match source, input with
            | Some source, TaskInputId.Target InputTarget.AttachmentName -> Some source.Name
            | Some source, TaskInputId.Target InputTarget.AttachmentLength ->
                Some(source.Bytes.Length.ToString(Globalization.CultureInfo.InvariantCulture))
            | Some source, TaskInputId.Target InputTarget.AttachmentDigest ->
                Some(AttachmentDigest.value source.Digest)
            | _ ->
                match options.TryGetValue(flag input) with
                | true, values -> values |> List.tryHead |> Option.flatten
                | _ -> None

        let acquire input =
            match input.Argument, remaining with
            | CommandArgument.Operand, _ when
                input.Name = target (InputTarget.Field BusinessField.CaseReference)
                && CommandGrammar.selectsExistingCase task
                && options.ContainsKey(flag TaskInputId.CaseId)
                ->
                None
            | CommandArgument.Operand, value :: rest ->
                remaining <- rest
                Some value
            | CommandArgument.Operand, [] ->
                problems.Add(
                    UsageError.MissingOperand(
                        CommandGrammar.inputName input.Name
                        |> CommandGrammar.kebab
                        |> _.ToUpperInvariant()
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

                if
                    acquired.IsNone
                    && input.Required
                    && input.Argument = CommandArgument.Named
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
                                let minimum, maximum =
                                    SubmissionDeclarations.integerBounds
                                        (TaskRouting.operationDeclaration kind)
                                        input.Name

                                match
                                    VerbOptions.integer (flag input.Name) minimum maximum value
                                with
                                | Ok number -> SubmissionValue.Integer number
                                | Error problem ->
                                    problems.Add problem
                                    SubmissionValue.Integer 0L
                            | TaskAtom.Boolean ->
                                match Boolean.TryParse value with
                                | true, value -> SubmissionValue.Boolean value
                                | _ ->
                                    problems.Add(UsageError.InvalidBoolean(flag input.Name))
                                    SubmissionValue.Boolean false
                            | TaskAtom.Text -> SubmissionValue.Text value

                        input.Name, scalar))
            |> Map.ofList

        for input in declarations do
            match input.Name with
            | TaskInputId.Target(InputTarget.Field field) when values.ContainsKey input.Name ->
                let field = BusinessField.describe field

                match field.Pairs with
                | Some paired when not (values.ContainsKey(target (InputTarget.Field paired))) ->
                    problems.Add(UsageError.MissingOption(flag (target (InputTarget.Field paired))))
                | _ -> ()
            | _ -> ()

        remaining |> List.iter (UsageError.ExtraOperand >> problems.Add)

        let text input =
            match Map.tryFind input values with
            | Some(SubmissionValue.Text value) -> Some value
            | _ -> None

        let uuid input =
            text input
            |> Option.bind (fun value ->
                match VerbOptions.uuid (flag input) value with
                | Ok id -> Some id
                | Error problem ->
                    problems.Add problem
                    None)

        let revision input =
            match Map.tryFind (target input) values with
            | Some(SubmissionValue.Integer value) -> Some value
            | _ -> None

        let selection () =
            uuid TaskInputId.CaseId |> ignore

            SubmissionAssembly.caseSelection values
            |> Result.defaultWith (fun _ ->
                if (text TaskInputId.CaseId).IsNone then
                    problems.Add(UsageError.InvalidValue(flag TaskInputId.CaseId, ""))

                CaseSelector.Reference "")

        let binding =
            SubmissionAssembly.draftBinding
                (TaskRouting.operationDeclaration kind)
                (selection ())
                revision
                (fun input -> uuid input)

        let authored =
            declarations
            |> List.filter (fun input -> input.Declaration.Source = InputSource.Authored)
            |> List.choose (fun input ->
                Map.tryFind input.Name values |> Option.map (fun value -> input.Name, value))
            |> Map.ofList

        let authored =
            SubmissionAssembly.withDefaults (TaskRouting.operationDeclaration kind) authored

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

                    let grantRow (value: string) =
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
                        |> Map.ofList

                    let partyRow (value: string) =
                        let fields =
                            collection.Members
                            |> List.map (fun input -> TaskInputId.token input.Input, input)
                            |> Map.ofList

                        let denied reason =
                            problems.Add(UsageError.InvalidCollection(name, reason))
                            Map.empty

                        match
                            StrictJson.readClassified
                                8192
                                (System.Text.Encoding.UTF8.GetBytes value)
                        with
                        | Error failure ->
                            match failure.Kind with
                            | JsonFailureReason.NotJson -> denied CollectionProblem.MalformedJson
                            | JsonFailureReason.DuplicateKey ->
                                denied CollectionProblem.DuplicateMember
                            | JsonFailureReason.TooLarge -> denied CollectionProblem.TooLarge
                            | JsonFailureReason.Schema -> denied CollectionProblem.InvalidMember
                        | Ok(JsonValue.Object values) ->
                            if values |> List.exists (fst >> fields.ContainsKey >> not) then
                                denied CollectionProblem.UnknownMember
                            else
                                let admit (key, atom) =
                                    let field = fields[key]

                                    match atom, field.Atom, field.Clearable with
                                    | JsonValue.String text, TaskAtom.Text, _ ->
                                        let choices =
                                            TaskChoiceId.ofInput field.Input
                                            |> Option.map TaskChoiceId.tokens
                                            |> Option.defaultValue []

                                        if choices.IsEmpty || List.contains text choices then
                                            Some(field.Input, SubmissionValue.Text text)
                                        else
                                            None
                                    | JsonValue.Boolean flag, TaskAtom.Boolean, _ ->
                                        Some(field.Input, SubmissionValue.Boolean flag)
                                    | JsonValue.Integer number, TaskAtom.Integer, _ ->
                                        Some(field.Input, SubmissionValue.Integer number)
                                    | JsonValue.Null, _, true ->
                                        Some(field.Input, SubmissionValue.Clear)
                                    | _ -> None

                                let members = values |> List.choose admit

                                if members.Length = values.Length then
                                    Map.ofList members
                                else
                                    denied CollectionProblem.InvalidMember
                        | Ok _ -> denied CollectionProblem.ExpectedObject

                    let parse =
                        match collection.Collection with
                        | TaskCollectionId.PeopleGrants -> grantRow
                        | TaskCollectionId.Parties -> partyRow

                    if roles.Length > collection.MaximumItems then
                        problems.Add(
                            UsageError.InvalidCollectionCount(
                                name,
                                collection.MinimumItems,
                                collection.MaximumItems
                            )
                        )

                    if
                        roles.Length > collection.MaximumItems
                        || (roles.IsEmpty && collection.MinimumItems = 0)
                    then
                        values
                    else
                        values
                        |> Map.add collection.Name (SubmissionValue.Rows(List.map parse roles)))
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
            | OperationBinding.CaseNew revision -> version revision
            | OperationBinding.Case(target, revision, payment, family) ->
                selected target
                :: (version revision
                    @ id "paymentId" payment
                    @ (family
                       |> Option.map (fun value ->
                           "expectedFamilySequence", JsonValue.Integer value)
                       |> Option.toList))
            | OperationBinding.Work(target, revision) -> selected target :: version revision
            | OperationBinding.Note(target, note) -> selected target :: id "noteId" note
            | OperationBinding.Attachment(target, attachment) ->
                selected target :: id "attachmentId" attachment
            | OperationBinding.Party(target, current) ->
                selected target
                :: (current
                    |> Option.map (fun (party, revision) ->
                        id "partyId" (Some party) @ version revision)
                    |> Option.defaultValue [])
            | OperationBinding.Link(target, link) -> selected target :: id "linkId" link
            | OperationBinding.Reminder(target, current) ->
                selected target
                :: (current
                    |> Option.map (fun (reminder, revision) ->
                        id "reminderId" (Some reminder) @ version revision)
                    |> Option.defaultValue [])
            | OperationBinding.People(actor, revision) -> id "actorId" actor @ version revision

        JsonValue.Object(
            id "operationId" draft.OperationId
            @ [
                "task", JsonValue.String(TaskId.token draft.Task)
                "binding",
                JsonValue.Object(
                    ("kind",
                     JsonValue.String(
                         ActSubject.token ((TaskRouting.operationDeclaration draft.Task).Target)
                     ))
                    :: binding
                )
                "inputs", inputs draft.Inputs
            ]
        )
