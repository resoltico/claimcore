namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open System.Collections.Generic
open System.Globalization
open ClaimCore.Json

[<RequireQualifiedAccess>]
type VerbFormat =
    | Text
    | Json
    | Csv

[<NoEquality; NoComparison>]
type PreparedVerb =
    {
        Invocation: Invocation
        DryRun: bool
        Confirmation: string option
        Format: VerbFormat
        All: bool
        Language: string
    }

    member this.Bytes =
        let bytes = Frames.encodeInvocation this.Invocation

        if this.DryRun && Frames.isProvisional this.Invocation then
            let document =
                StrictJson.read Limits.MaximumInvocationBytes bytes
                |> Result.defaultWith (fun _ -> invalidOp "An encoded frame must decode.")

            match document with
            | JsonValue.Object fields ->
                fields
                |> List.map (fun (key, value) ->
                    key,
                    if key = "input" then
                        match value with
                        | JsonValue.Object input ->
                            JsonValue.Object(input |> List.filter (fst >> ((<>) "operationId")))
                        | _ -> value
                    else
                        value)
                |> JsonValue.Object
                |> WireWriter.bytes
            | _ -> bytes
        else
            bytes

module Grammar =
    open VerbOptions

    let private flag name =
        "--" + CommandGrammar.kebab (CommandGrammar.inputName name)

    let private operand name =
        CommandGrammar.kebab (CommandGrammar.inputName name) |> _.ToUpperInvariant()

    let private errorKey =
        function
        | UsageError.UnknownOption(flag, _)
        | UsageError.MissingOption flag
        | UsageError.InvalidValue(flag, _)
        | UsageError.InvalidChoice(flag, _, _)
        | UsageError.NotApplicable(flag, _)
        | UsageError.DuplicateOption flag
        | UsageError.MissingValue flag -> Some flag
        | _ -> None

    let private priority =
        function
        | UsageError.DuplicateOption _ -> 5
        | UsageError.MissingValue _ -> 4
        | UsageError.InvalidChoice _
        | UsageError.InvalidValue _ -> 3
        | UsageError.MissingOption _ -> 0
        | _ -> 2

    let private errors (values: ResizeArray<UsageError>) =
        values
        |> Seq.distinct
        |> Seq.groupBy errorKey
        |> Seq.collect (fun (key, group) ->
            if key.IsNone then
                group
            else
                group |> Seq.sortByDescending priority |> Seq.truncate 1)
        |> Seq.toList

    let private scalarValue
        newId
        (item: CatalogDerivedInput)
        name
        raw
        (problems: ResizeArray<UsageError>)
        =
        match raw with
        | Some value -> Some(atom name item.Atom value problems)
        | None when item.Binding.Source = InputSource.OperationIdentity ->
            Some(JsonValue.String(newId ()))
        | None when item.Binding.Default.IsSome -> Some(bindingValue item.Binding.Default.Value)
        | None when item.Binding.Required ->
            problems.Add(
                if item.Argument = CatalogArgument.Operand then
                    UsageError.MissingOperand name
                else
                    UsageError.MissingOption name
            )

            Some JsonValue.Null
        | None -> None

    let private build
        incomplete
        newId
        entry
        (options: Dictionary<string, string option list>)
        operands
        (problems: ResizeArray<UsageError>)
        =
        let derived, constants = CommandGrammar.derive entry
        let used = HashSet<string>()
        let selectors = Dictionary<string, string>()
        let mutable position = 0
        let mutable body = JsonValue.Object []

        let applies branches = VerbOptions.applies branches body
        let assign path value = body <- insert path value body
        let fixedValues () = body <- assignConstants constants body

        fixedValues ()

        let option name =
            match options.TryGetValue name with
            | true, value -> value |> List.tryHead |> Option.flatten
            | _ -> None

        let input (item: CatalogDerivedInput) =
            let name, raw =
                match item.Argument with
                | CatalogArgument.Operand ->
                    let value = List.tryItem position operands
                    position <- position + 1
                    operand item.Name, value
                | _ ->
                    let name = flag item.Name
                    used.Add name |> ignore
                    name, option name

            match item.Atom, options.TryGetValue name with
            | CatalogAtom.Choices choices, (true, values) ->
                values
                |> List.rev
                |> List.choose id
                |> List.map (fun value -> atom name (CatalogAtom.Choice choices) value problems)
                |> List.distinct
                |> JsonValue.Array
                |> Some
            | CatalogAtom.Choices _, _ -> None
            | _ when
                incomplete
                && raw.IsNone
                && (item.Binding.Source = InputSource.OperationIdentity
                    || item.Binding.Source = InputSource.ExpectedRevision)
                ->
                None
            | _ when
                item.Binding.AllowsNull
                && options.ContainsKey(
                    "--clear-" + CommandGrammar.kebab (CommandGrammar.inputName item.Name)
                )
                ->
                if raw.IsSome then
                    problems.Add(UsageError.DuplicateOption name)

                Some JsonValue.Null
            | _ -> scalarValue newId item name raw problems

        for item in derived do
            if applies item.Binding.Branches then
                let value =
                    match item.Binding.Kind with
                    | BindingKind.Group(_, choices) when item.Argument = CatalogArgument.Implicit ->
                        let supplied choice =
                            derived
                            |> List.exists (fun child ->
                                child.Argument = CatalogArgument.Named
                                && options.ContainsKey(flag child.Name)
                                && child.Binding.Branches
                                   |> List.exists (fun branch ->
                                       branch.Path = item.Binding.Path && branch.Value = choice))

                        choices
                        |> List.tryFind supplied
                        |> Option.defaultValue choices.Head
                        |> JsonValue.String
                        |> Some
                    | BindingKind.Group(_, choices) ->
                        match input item with
                        | Some(JsonValue.String value) -> Some(JsonValue.String value)
                        | None when not item.Binding.Required && item.Binding.Default.IsNone -> None
                        | _ -> Some(JsonValue.String choices.Head)
                    | _ -> input item

                value
                |> Option.iter (fun value ->
                    assign item.Binding.Path value

                    match item.Binding.Kind, value with
                    | BindingKind.Group _, JsonValue.String token ->
                        selectors[String.concat "." item.Binding.Path] <- token
                    | _ -> ())

                fixedValues ()

        for definition in CommandGrammar.collections entry do
            if applies definition.Binding.Branches then
                let name = CommandGrammar.collectionFlag definition
                used.Add name |> ignore

                let values =
                    match options.TryGetValue name with
                    | true, values -> values |> List.rev |> List.choose id
                    | _ -> []

                if definition.Binding.Required || options.ContainsKey name then
                    assign
                        definition.Binding.Path
                        (CollectionArguments.build definition values problems)

        for value in operands |> List.skip (min position operands.Length) do
            problems.Add(UsageError.ExtraOperand value)

        for item in derived do
            let name = flag item.Name

            if
                item.Argument = CatalogArgument.Named
                && options.ContainsKey name
                && not (used.Contains name)
            then
                let because =
                    item.Binding.Branches
                    |> List.tryPick (fun branch ->
                        match selectors.TryGetValue(String.concat "." branch.Path) with
                        | true, value when value <> branch.Value ->
                            derived
                            |> List.tryFind (fun selector -> selector.Binding.Path = branch.Path)
                            |> Option.map (fun selector ->
                                flag selector.Name + " " + CommandGrammar.kebab value)
                        | _ -> None)
                    |> Option.defaultValue "the selected command"

                problems.Add(UsageError.NotApplicable(name, because))

        body

    let private prepareWith incomplete admit finish newId arguments =
        let found =
            CommandGrammar.tryOperation arguments
            |> Option.map (fun entry ->
                entry, List.skip (CommandGrammar.words entry).Length arguments)

        match found with
        | None ->
            let count =
                if
                    arguments
                    |> List.tryHead
                    |> Option.exists (fun word -> List.contains word CommandGrammar.families)
                then
                    2
                else
                    1

            let value = String.concat " " (List.truncate count arguments)

            Error
                [
                    UsageError.UnknownCommand(
                        value,
                        suggestions
                            value
                            (CommandGrammar.operations
                             |> List.map (CommandGrammar.words >> String.concat " "))
                    )
                ]
        | Some(entry, arguments) ->
            let problems = ResizeArray<UsageError>()
            let inputs, _ = CommandGrammar.derive entry

            let named =
                inputs |> List.filter (fun input -> input.Argument = CatalogArgument.Named)

            let clearFlags =
                named
                |> List.choose (fun input ->
                    if CommandGrammar.canClear entry input then
                        Some(
                            "--clear-" + CommandGrammar.kebab (CommandGrammar.inputName input.Name)
                        )
                    else
                        None)

            let collectionFlags =
                CommandGrammar.collections entry |> List.map CommandGrammar.collectionFlag

            let flags =
                (named |> List.map (fun input -> flag input.Name))
                @ clearFlags
                @ collectionFlags

            let switches = clearFlags

            let repeated =
                named
                |> List.choose (fun input ->
                    match input.Atom with
                    | CatalogAtom.Choices _ -> Some(flag input.Name)
                    | _ -> None)

            let repeated = repeated @ collectionFlags

            let options, operands =
                tokenize
                    (CommandGrammar.globalFlags @ flags)
                    ((CommandGrammar.globalOptions entry |> List.map fst) @ flags)
                    (CommandGrammar.globalSwitches @ switches)
                    repeated
                    arguments
                    problems

            let allocate () =
                if options.ContainsKey "--dry-run" then
                    "00000000-0000-7000-8000-000000000000"
                else
                    newId ()

            let body = build incomplete allocate entry options operands problems

            let endpoint =
                if options.ContainsKey "--preview" && entry.SelectedAct.IsSome then
                    Api.operationPreview.Token
                else
                    ActorEndpoint.token entry.Endpoint

            if options.ContainsKey "--preview" && entry.SelectedAct.IsNone then
                problems.Add(
                    UsageError.NotApplicable(
                        "--preview",
                        CommandGrammar.words entry |> String.concat " "
                    )
                )

            let option name fallback =
                match options.TryGetValue name with
                | true, [ Some value ] -> value
                | _ -> fallback

            let format = option "--format" "text"

            let language = option "--language" "en"

            let listing = CommandGrammar.supportsAll entry

            let formats = CommandGrammar.formats entry

            if not (List.contains format formats) then
                problems.Add(UsageError.InvalidChoice("--format", format, formats))

            if options.ContainsKey "--all" && not listing then
                problems.Add(
                    UsageError.NotApplicable(
                        "--all",
                        String.concat " " (CommandGrammar.words entry)
                    )
                )

            if not (List.contains language LocalizationPolicy.languages) then
                problems.Add(
                    UsageError.InvalidChoice("--language", language, LocalizationPolicy.languages)
                )

            if
                options.ContainsKey "--confirm"
                && CatalogOperation.consequence entry <> Consequence.Irreversible
            then
                problems.Add(
                    UsageError.NotApplicable(
                        "--confirm",
                        String.concat " " (CommandGrammar.words entry)
                    )
                )

            let found = errors problems

            if not found.IsEmpty then
                Error found
            else
                let admitted = admit endpoint body

                match admitted with
                | Error problem ->
                    let path = problem.Path.TrimStart('$', '.')
                    let input = inputs |> List.tryFind (fun input -> input.Binding.Identity = path)

                    let name =
                        input
                        |> Option.map (fun input ->
                            if input.Argument = CatalogArgument.Operand then
                                operand input.Name
                            else
                                flag input.Name)
                        |> Option.defaultValue path

                    let raw =
                        let rec at value =
                            function
                            | [] ->
                                match value with
                                | JsonValue.String value -> value
                                | JsonValue.Integer value -> string value
                                | _ -> ""
                            | key :: rest ->
                                JsonValue.tryMember key value
                                |> Option.map (fun value -> at value rest)
                                |> Option.defaultValue ""

                        at body (path.Split('.') |> List.ofArray)

                    let issue =
                        match input with
                        | Some input when input.Atom = CatalogAtom.Integer ->
                            UsageError.InvalidValue(name, raw)
                        | _ -> UsageError.InvalidChoice(name, raw, [])

                    Error [ issue ]
                | Ok input ->
                    finish
                        input
                        (options.ContainsKey "--dry-run")
                        (if format = "csv" then VerbFormat.Csv
                         elif format = "json" then VerbFormat.Json
                         else VerbFormat.Text)
                        (options.ContainsKey "--all")
                        language
                        (options.ContainsKey "--preview")
                        (match options.TryGetValue "--confirm" with
                         | true, [ Some value ] -> Some value
                         | _ -> None)

    let prepare newId arguments =
        let admit endpoint body =
            Api.tryFind endpoint
            |> Option.defaultWith (fun () -> invalidOp "Unknown catalog endpoint")
            |> fun endpoint -> endpoint.ReadInput body

        let finish input dryRun format all language _ confirmation =
            Frames.fromInput input
            |> Result.mapError (fun _ -> [ UsageError.RequestTooLarge Limits.MaximumRequestBytes ])
            |> Result.map (fun invocation ->
                {
                    Invocation =
                        if
                            (match (Frames.endpoint invocation).Id with
                             | EndpointId.Actor(ActorEndpoint.Operation _) -> false
                             | _ -> true)
                            || (arguments
                                |> List.exists (fun word ->
                                    word = "--operation-id"
                                    || word.StartsWith(
                                        "--operation-id=",
                                        System.StringComparison.Ordinal
                                    )))
                        then
                            invocation
                        else
                            Frames.provisional invocation
                    DryRun = dryRun
                    Confirmation = confirmation
                    Format = format
                    All = all
                    Language = language
                })

        prepareWith false admit finish newId arguments

    /// Authored intent is admitted without inventing an executable operation or revision.
    let private intent targetCodec actCodec arguments =
        let codec =
            Codec.record (fun reference act operation -> reference, act, operation)
            |> Codec.field "target" (fun (target, _, _) -> target) targetCodec
            |> Codec.field "act" (fun (_, act, _) -> act) actCodec
            |> Codec.optionalField "operationId" (fun (_, _, operation) -> operation) Codec.uuid
            |> Codec.build

        let admit _ body =
            // The enclosing subject tag is a catalog constant, not part of authored intent.
            let fields =
                match body with
                | JsonValue.Object fields ->
                    fields
                    |> List.filter (fun (name, _) ->
                        name <> WireMembers.Kind && name <> WireMembers.ExpectedRevision)
                | _ -> []

            codec.Read "$" (JsonValue.Object fields)

        let finish input _ _ _ language preview _ = Ok(input, language, preview)

        prepareWith
            true
            admit
            finish
            (fun () -> invalidOp "Intent does not allocate an operation ID.")
            arguments

    let internal caseIntent arguments =
        intent RequestCodecs.caseSelector RequestCodecs.caseAct arguments

    let internal workIntent arguments =
        intent RequestCodecs.caseSelector RequestCodecs.workAct arguments

    let internal reminderIntent arguments =
        let target =
            Codec.record (fun id revision -> id, revision)
            |> Codec.field "reminderId" fst Codec.uuid
            |> Codec.optionalField "expectedRevision" snd RequestCodecs.reminderRevision

        intent RequestCodecs.caseSelector (RequestCodecs.reminderActWith target) arguments


    let internal authorityIntent arguments =
        intent RequestCodecs.principal RequestCodecs.authorityAct arguments
