namespace ClaimCore.Cli

open System
open System.Collections.Generic
open System.Globalization
open ClaimCore.CommandLine
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Json

/// Query and command arguments follow their declarations; the endpoint codec admits the typed request.
module internal QueryArguments =
    let build
        entry
        (options: Dictionary<string, string option list>)
        operands
        (problems: ResizeArray<UsageError>)
        =
        let declaration =
            EndpointDeclarations.find entry
            |> Option.defaultWith (fun () ->
                invalidArg (nameof entry) "An endpoint declaration is required.")

        let used = HashSet<string>()
        let mutable consumed = 0
        let supplied = ResizeArray<TaskInputId * string>()

        let flag input =
            "--" + CommandGrammar.kebab (CommandGrammar.inputName input)

        let acquire (input: CommandInput) =
            match input.Argument with
            | CommandArgument.Operand ->
                let value = List.tryItem consumed operands
                consumed <- consumed + 1

                if value.IsNone then
                    problems.Add(
                        UsageError.MissingOperand(
                            CommandGrammar.kebab (CommandGrammar.inputName input.Name)
                            |> _.ToUpperInvariant()
                        )
                    )

                value |> Option.toList
            | CommandArgument.Named ->
                let name = flag input.Name
                used.Add name |> ignore

                let values =
                    match options.TryGetValue name with
                    | true, values -> values |> List.rev |> List.choose id
                    | _ -> []

                if values.IsEmpty && input.Required then
                    problems.Add(UsageError.MissingOption name)

                values
            | CommandArgument.Implicit -> []

        let choice name choices value =
            choices
            |> List.tryFind (fun token -> value = token || value = CommandGrammar.kebab token)
            |> Option.orElseWith (fun () ->
                problems.Add(
                    UsageError.InvalidChoice(
                        flag name,
                        value,
                        choices |> List.map CommandGrammar.kebab
                    )
                )

                None)

        let uuid name value =
            match Guid.TryParseExact(value, "D") with
            | true, value when value <> Guid.Empty -> Some(Codec.uuid.Write value)
            | _ ->
                problems.Add(UsageError.InvalidChoice(flag name, value, []))
                None

        let scalar (input: CommandInput) value =
            let name = input.Name

            match input.Atom with
            | CommandAtom.Choice choices -> choice name choices value |> Option.map JsonValue.String
            | CommandAtom.Boolean ->
                match value with
                | "true" -> Some(JsonValue.Boolean true)
                | "false" -> Some(JsonValue.Boolean false)
                | _ ->
                    problems.Add(UsageError.InvalidChoice(flag name, value, [ "true"; "false" ]))
                    None
            | CommandAtom.Integer ->
                let maximum =
                    declaration.Page
                    |> Option.map _.MaximumLimit
                    |> Option.defaultValue Paging.MaximumPageSize

                match
                    Int64.TryParse(
                        value,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture
                    )
                with
                | true, number when number >= 1L && number <= int64 maximum ->
                    Some(JsonValue.Integer number)
                | _ ->
                    problems.Add(UsageError.InvalidRange(flag name, value, 1, maximum))
                    None
            | CommandAtom.Text ->
                match name with
                | TaskInputId.Target(InputTarget.Field BusinessField.CaseReference) ->
                    Some(RequestCodecs.caseSelector.Write(CaseSelector.Reference value))
                | TaskInputId.ActorId
                | TaskInputId.ResponsibleActorFilter
                | TaskInputId.Target(InputTarget.OperationId | InputTarget.ReminderId) ->
                    uuid name value
                | TaskInputId.Target InputTarget.Cursor ->
                    let maximum =
                        declaration.Page
                        |> Option.map _.MaximumCursorCharacters
                        |> Option.defaultValue Paging.MaximumActorCursorCharacters

                    match (Codec.boundedText 1 maximum).Read "$" (JsonValue.String value) with
                    | Ok value -> Some(JsonValue.String value)
                    | Error _ ->
                        problems.Add(UsageError.InvalidValue(flag name, value))
                        None
                | TaskInputId.Target InputTarget.DirectorySearch ->
                    match
                        (Codec.boundedText 1 DirectorySearchText.MaximumCharacters).Read
                            "$"
                            (JsonValue.String value)
                    with
                    | Ok value -> Some(JsonValue.String value)
                    | Error _ ->
                        problems.Add(UsageError.InvalidValue(flag name, value))
                        None
                | _ -> Some(JsonValue.String value)
            | CommandAtom.Choices _ -> invalidOp "Multiple choices are acquired together."

        let members =
            CommandGrammar.inputs entry
            |> List.choose (fun input ->
                let values = acquire input
                values |> List.iter (fun value -> supplied.Add(input.Name, value))

                let value =
                    match input.Atom with
                    | CommandAtom.Choices choices when not values.IsEmpty ->
                        values
                        |> List.choose (choice input.Name choices)
                        |> List.distinct
                        |> List.map JsonValue.String
                        |> JsonValue.Array
                        |> Some
                    | _ -> values |> List.tryHead |> Option.bind (scalar input)

                value
                |> Option.map (fun value ->
                    EndpointInputs.memberName declaration input.Name, value))

        let members =
            match declaration.Page with
            | Some page when
                not (members |> List.exists (fun (name, _) -> name = WireMembers.Limit))
                ->
                members @ [ WireMembers.Limit, JsonValue.Integer(int64 page.DefaultLimit) ]
            | _ -> members

        operands
        |> List.skip (min consumed operands.Length)
        |> List.iter (UsageError.ExtraOperand >> problems.Add)

        for name in options.Keys do
            if not (used.Contains name || List.contains name CommandGrammar.globalFlags) then
                problems.Add(UsageError.NotApplicable(name, declaration.Id))

        if problems.Count > 0 then
            None
        else
            let route =
                Api.tryFind declaration.Id
                |> Option.defaultWith (fun () -> invalidOp "The declared endpoint is unavailable.")

            match route.ReadInput(JsonValue.Object members) with
            | Ok input -> Some input
            | Error problem ->
                let argument =
                    supplied
                    |> Seq.tryFind (fun (input, _) ->
                        problem.Path.StartsWith(
                            "$." + EndpointInputs.memberName declaration input,
                            StringComparison.Ordinal
                        ))

                match argument with
                | Some(input, value) -> problems.Add(UsageError.InvalidValue(flag input, value))
                | None -> problems.Add(UsageError.InvalidValue("command", problem.Reason))

                None
