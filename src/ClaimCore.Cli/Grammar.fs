namespace ClaimCore.Cli

open ClaimCore.Domain
open ClaimCore.Protocol

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
        Format: VerbFormat
        All: bool
        Language: string
    }

    member this.Bytes = Frames.encodeInvocation this.Invocation

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

    let private constant =
        function
        | BindingValue.Text value -> JsonValue.String value
        | BindingValue.Integer value -> JsonValue.Integer value
        | BindingValue.Boolean value -> JsonValue.Boolean value

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
        | None when item.Binding.Default.IsSome -> Some(constant item.Binding.Default.Value)
        | None when item.Binding.Required ->
            problems.Add(
                if item.Argument = CatalogArgument.Operand then
                    UsageError.MissingOperand name
                else
                    UsageError.MissingOption name
            )

            Some JsonValue.Null
        | None ->
            match item.Atom with
            | CatalogAtom.Switch(off, _) -> Some(JsonValue.String off)
            | _ -> None

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

        let rec at path value =
            match path with
            | [] -> Some value
            | key :: rest -> JsonValue.tryMember key value |> Option.bind (at rest)

        let rec insert path value current =
            match path, current with
            | [], _ -> value
            | key :: rest, JsonValue.Object fields ->
                let previous =
                    fields
                    |> List.tryFind (fst >> (=) key)
                    |> Option.map snd
                    |> Option.defaultValue (JsonValue.Object [])

                let next = insert rest value previous

                JsonValue.Object(
                    if fields |> List.exists (fst >> (=) key) then
                        fields
                        |> List.map (fun (name, old) -> name, if name = key then next else old)
                    else
                        fields @ [ key, next ]
                )
            | _ -> invalidOp "A catalog path must traverse objects."

        let applies (branches: BindingCondition list) =
            branches
            |> List.forall (fun branch -> at branch.Path body = Some(JsonValue.String branch.Value))

        let assign path value = body <- insert path value body

        let fixedValues () =
            for item in constants do
                if applies item.Branches then
                    assign item.Path (constant item.Value)

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
                    || item.Binding.Identity = "expectedRevision")
                ->
                None
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
            (Catalog.document.Entries |> List.map OperationCatalog.operation)
            |> List.tryPick (fun entry ->
                let words = CommandGrammar.words entry

                if List.truncate words.Length arguments = words then
                    Some(entry, List.skip words.Length arguments)
                else
                    None)

        match found with
        | None ->
            let value = String.concat " " (List.truncate 2 arguments)

            Error
                [
                    UsageError.UnknownCommand(
                        value,
                        suggestions
                            value
                            ((Catalog.document.Entries |> List.map OperationCatalog.operation)
                             |> List.map (CommandGrammar.words >> String.concat " "))
                    )
                ]
        | Some(entry, arguments) ->
            let problems = ResizeArray<UsageError>()
            let inputs, _ = CommandGrammar.derive entry

            let named =
                inputs |> List.filter (fun input -> input.Argument = CatalogArgument.Named)

            let flags = named |> List.map (fun input -> flag input.Name)

            let switches =
                named
                |> List.choose (fun input ->
                    match input.Atom with
                    | CatalogAtom.Switch _ -> Some(flag input.Name)
                    | _ -> None)

            let repeated =
                named
                |> List.choose (fun input ->
                    match input.Atom with
                    | CatalogAtom.Choices _ -> Some(flag input.Name)
                    | _ -> None)

            let options, operands =
                tokenize
                    ("--dry-run" :: "--preview" :: "--all" :: "--format" :: "--language" :: flags)
                    ("--dry-run" :: "--preview" :: "--all" :: switches)
                    repeated
                    arguments
                    problems

            let body = build incomplete newId entry options operands problems

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

            let listing = entry.Endpoint = ActorEndpoint.Query QueryEndpoint.CaseList

            let formats =
                if
                    listing || entry.Endpoint = ActorEndpoint.Query QueryEndpoint.AuthorityHistory
                then
                    [ "text"; "json"; "csv" ]
                else
                    [ "text"; "json" ]

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

    let prepare newId arguments =
        let admit endpoint body =
            Api.tryFind endpoint
            |> Option.defaultWith (fun () -> invalidOp "Unknown catalog endpoint")
            |> fun endpoint -> endpoint.ReadInput body

        let finish input dryRun format all language _ =
            Frames.fromInput input
            |> Result.mapError (fun _ -> [ UsageError.RequestTooLarge Limits.MaximumRequestBytes ])
            |> Result.map (fun invocation ->
                {
                    Invocation = invocation
                    DryRun = dryRun
                    Format = format
                    All = all
                    Language = language
                })

        prepareWith false admit finish newId arguments

    /// Authored intent is admitted without inventing an executable operation or revision.
    let internal caseIntent arguments =
        let codec =
            Codec.record (fun reference act operation -> reference, act, operation)
            |> Codec.field "target" (fun (target, _, _) -> target) RequestCodecs.caseSelector
            |> Codec.field "act" (fun (_, act, _) -> act) RequestCodecs.caseAct
            |> Codec.optionalField "operationId" (fun (_, _, operation) -> operation) Codec.uuid
            |> Codec.build

        let admit _ body =
            // The enclosing subject tag is a catalog constant, not part of case intent.
            let fields =
                match body with
                | JsonValue.Object fields -> fields |> List.filter (fst >> (<>) WireMembers.Kind)
                | _ -> []

            codec.Read "$" (JsonValue.Object fields)

        let finish input _ _ _ language preview = Ok(input, language, preview)

        prepareWith
            true
            admit
            finish
            (fun () -> invalidOp "Intent does not allocate an operation ID.")
            arguments
