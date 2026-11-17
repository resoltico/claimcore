namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Presentation
open ClaimCore.Wire
open ClaimCore.Json

/// Command spelling and quoting belong to the terminal; selection and availability are supplied.
module internal ControlHints =
    let private flag input =
        "--" + CommandGrammar.kebab (CommandGrammar.inputName input)

    let private target input = TaskInputId.Target input
    let private reference = target (InputTarget.Field BusinessField.CaseReference)

    let private argument value =
        // An unsafe terminal scalar is represented by an explicit acquisition placeholder.
        (if TerminalText.data value = value then value else "<value>")
        |> CommandGrammar.quoteArgument

    let private atom =
        function
        | InputValue.Text value -> value
        | InputValue.Integer value -> string value
        | InputValue.Boolean value -> if value then "true" else "false"
        | InputValue.Null -> "<clear>"

    let private selection =
        function
        | CaseSelector.Id id -> [ TaskInputId.CaseId, CaseId.format id ]
        | CaseSelector.Reference value -> [ reference, value ]

    let private version input value = target input, Revision.format value

    let private binding =
        function
        | Binding.CaseNew _ -> []
        | Binding.Case(value, revision) ->
            selection value @ [ version InputTarget.ExpectedRevision revision ]
        | Binding.CasePayment(value, revision, payment) ->
            selection value
            @ [
                version InputTarget.ExpectedRevision revision
                target InputTarget.PaymentId, OperationId.format payment
            ]
        | Binding.CaseErase(value, revision, family) ->
            selection value
            @ [
                version InputTarget.ExpectedRevision revision
                target InputTarget.ExpectedFamilySequence, Sequence.format family
            ]
        | Binding.Work(value, revision) ->
            selection value @ [ version InputTarget.ExpectedWorkRevision revision ]
        | Binding.NoteNew value
        | Binding.ReminderNew value -> selection value
        | Binding.Note(value, id) ->
            selection value @ [ target InputTarget.NoteId, NoteId.format id ]
        | Binding.Reminder(value, id, revision) ->
            selection value
            @ [
                target InputTarget.ReminderId, ReminderId.format id
                version InputTarget.ExpectedReminderRevision revision
            ]
        | Binding.PeopleNew _ -> []
        | Binding.People(id, revision) ->
            [
                TaskInputId.ActorId, ActorId.format id
                version InputTarget.ExpectedRevision revision
            ]

    let private command task values suffix =
        let values =
            values
            |> List.groupBy fst
            |> List.map (fun (key, values) -> key, List.map snd values)
            |> Map.ofList

        let inputs = CommandGrammar.inputs task

        let values =
            if (TaskDeclarations.describe task).Kind = ClientTaskKind.Query then
                inputs
                |> List.fold
                    (fun values input ->
                        if
                            not input.Required
                            || Map.containsKey input.Name values
                            || (input.Name = reference && Map.containsKey TaskInputId.CaseId values)
                        then
                            values
                        else
                            Map.add
                                input.Name
                                [
                                    "<"
                                    + CommandGrammar.kebab (CommandGrammar.inputName input.Name)
                                    + ">"
                                ]
                                values)
                    values
            else
                values

        let acquire (input: CommandInput) =
            Map.tryFind input.Name values |> Option.defaultValue []

        let operands =
            inputs
            |> List.filter (fun input -> input.Argument = CommandArgument.Operand)
            |> List.collect acquire

        let named =
            inputs
            |> List.filter (fun input -> input.Argument = CommandArgument.Named)
            |> List.collect (fun input ->
                acquire input |> List.map (fun value -> flag input.Name + "=" + argument value))

        let rows =
            CommandGrammar.collections task
            |> List.collect (fun collection ->
                Map.tryFind collection.Name values
                |> Option.defaultValue []
                |> List.map (fun row ->
                    CommandGrammar.collectionFlag collection + "=" + argument row))

        let operands =
            (if
                 operands
                 |> List.exists (fun value ->
                     value.StartsWith("-", System.StringComparison.Ordinal))
             then
                 [ "--" ]
             else
                 [])
            @ (operands |> List.map argument)

        String.concat " " (CommandGrammar.commandText task :: named @ rows @ suffix @ operands)

    let action (rendering: RenderingContext) (value: Affordance) =
        let task = TaskId.token value.Task
        let label = ReplyTextFormat.label rendering value.LabelId

        match value.Blocked with
        | Some advice ->
            let args =
                advice.Message.Arguments
                |> Map.map (fun _ -> TextDescriptorValue.render rendering)

            [
                label
                + ": "
                + MessageCatalogs.render rendering.Language advice.Message.MessageId args
            ]
        | None ->
            let values, suffix =
                match value.Control with
                | ActionControl.Operation bound ->
                    let fixedValues =
                        value.Seeds
                        |> SubmissionAssembly.fixedInputs
                            (TaskRouting.operationDeclaration value.Task)
                            value.Editable
                        |> Map.toList
                        |> List.map (fun (key, value) ->
                            let declared =
                                (TaskDeclarations.describe task).Inputs
                                |> List.tryFind (fun input -> input.Input = key)

                            let privateText =
                                declared
                                |> Option.exists (fun input ->
                                    input.Retention = InputRetention.Erasable
                                    && (match key with
                                        | TaskInputId.Target(InputTarget.Field field) ->
                                            match FieldPresentation.descriptor field with
                                            | ScalarRule.Text _
                                            | ScalarRule.PlainText _ -> true
                                            | _ -> false
                                        | _ -> input.Atom = TaskAtom.Text))

                            key,
                            if privateText then
                                "<" + CommandGrammar.inputName key + ">"
                            else
                                atom value)

                    let editable =
                        value.Editable
                        |> List.choose (fun input ->
                            (TaskDeclarations.describe task).Inputs
                            |> List.tryFind (fun item ->
                                item.Input = input
                                && (item.Mode = TaskInputMode.Required
                                    || item.Mode = TaskInputMode.Update))
                            |> Option.map (fun _ ->
                                input, "<" + CommandGrammar.inputName input + ">"))

                    let rows =
                        CommandGrammar.collections task
                        |> List.filter (fun collection ->
                            List.contains collection.Name value.Editable)
                        |> List.collect (fun collection ->
                            List.replicate
                                collection.MinimumItems
                                (collection.Name,
                                 "<" + CommandGrammar.collectionPlaceholder collection + ">"))

                    binding bound @ fixedValues @ editable @ rows, [ "--preview" ]
                | ActionControl.Command(CommandRequest.DismissAccessRequest principal) ->
                    let principal = PrincipalKey.toInput principal

                    [
                        target InputTarget.PrincipalKind,
                        Vocabulary.token PrincipalKind.vocabulary principal.Kind
                        target InputTarget.PrincipalIssuer, principal.Issuer
                        target InputTarget.PrincipalSubject, principal.Subject
                    ],
                    [ "--dry-run" ]

                | ActionControl.Command CommandRequest.TerminateOwnSessions -> [], [ "--dry-run" ]
                | ActionControl.Command(CommandRequest.TerminatePersonSessions actor) ->
                    [ TaskInputId.ActorId, ActorId.format actor ], [ "--dry-run" ]

            [ label + ": " + command task values suffix ]

    let link (value: Link) =
        let task = QueryTaskId.key (Link.endpoint value)
        let encoded = PresentationCodecs.link.Write value

        match JsonValue.tryMember "input" encoded with
        | Some(JsonValue.Object members) ->
            let declaration = EndpointDeclarations.find task |> Option.get

            let values =
                members
                |> List.collect (fun (name, value) ->
                    if name = "target" then
                        match RequestCodecs.caseSelector.Read "$" value with
                        | Ok value -> selection value
                        | Error _ -> []
                    else
                        declaration.Inputs
                        |> List.tryFind (fun input ->
                            EndpointInputs.memberName declaration input.Input = name)
                        |> Option.map (fun input ->
                            let scalar =
                                function
                                | JsonValue.String value -> value
                                | JsonValue.Integer value -> string value
                                | JsonValue.Boolean value -> (if value then "true" else "false")
                                | _ -> "<value>"

                            match value with
                            | JsonValue.Array values ->
                                values |> List.map (fun value -> input.Input, scalar value)
                            | _ -> [ input.Input, scalar value ])
                        |> Option.defaultValue [])

            command task values []
        | _ -> command task [] [ "'<operation-id>'" ]
