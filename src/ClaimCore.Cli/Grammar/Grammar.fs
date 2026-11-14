namespace ClaimCore.Cli

open ClaimCore.CommandLine

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

open System
open ClaimCore.Json

[<RequireQualifiedAccess>]
type VerbFormat =
    | Text
    | Json
    | Csv

[<NoEquality; NoComparison>]
type OfflineDraft =
    {
        Bytes: byte array
        Language: string
        Format: VerbFormat
        Issues: InputIssues option
    }

[<NoEquality; NoComparison>]
type PreparedVerb =
    {
        Invocation: Invocation
        DryRun: bool
        Confirmation: string option
        Format: VerbFormat
        All: bool
        Details: bool
        Language: string
    }

    member this.Bytes = Frames.encodeInvocation this.Invocation

module Grammar =
    open VerbOptions

    let private flag name =
        "--" + CommandGrammar.kebab (CommandGrammar.inputName name)

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

    let private build
        incomplete
        allocate
        (entry: TaskId)
        options
        operands
        (problems: ResizeArray<UsageError>)
        =
        match (TaskRouting.act entry) with
        | _ when problems.Count > 0 -> Choice1Of2 None
        | Some kind ->
            let intent =
                TaskArguments.mutation incomplete allocate kind options operands problems

            Choice2Of2 intent
        | None ->
            match (TaskRouting.endpoint entry) with
            | ActorEndpoint.Query _
            | ActorEndpoint.Command _ ->
                QueryArguments.build entry options operands problems |> Some |> Choice1Of2
            | ActorEndpoint.Operation _ -> invalidOp "An operation verb requires its semantic task."

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
            let inputs = CommandGrammar.inputs entry

            let named =
                inputs |> List.filter (fun input -> input.Argument = CommandArgument.Named)

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
                    | CommandAtom.Choices _ -> Some(flag input.Name)
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

            let body =
                build
                    (incomplete || options.ContainsKey "--dry-run")
                    newId
                    entry
                    options
                    operands
                    problems

            let endpoint =
                if options.ContainsKey "--preview" && (TaskRouting.act entry).IsSome then
                    Api.operationPreview.Token
                else
                    ActorEndpoint.token (TaskRouting.endpoint entry)

            if options.ContainsKey "--preview" && (TaskRouting.act entry).IsNone then
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
                && TaskRouting.consequence entry <> Consequence.Irreversible
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
                | Error problem -> Error [ UsageError.InvalidValue("command", problem.Reason) ]
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
                        (options.ContainsKey "--details")
                        (match options.TryGetValue "--confirm" with
                         | true, [ Some value ] -> Some value
                         | _ -> None)

    let prepare newId arguments =
        let admit endpoint =
            function
            | Choice1Of2(Some input) -> Ok input
            | Choice1Of2 None -> invalidOp "Invalid argv must be reported before admission."
            | Choice2Of2 intent ->
                OperationSubmission.completeDraft intent
                |> Option.map (fun input ->
                    if endpoint = Api.operationPreview.Token then
                        EndpointValues.input Api.operationPreview input
                    else
                        EndpointValues.input Api.operationExecute input)
                |> Option.map Ok
                |> Option.defaultValue (
                    Error
                        {
                            Path = "$"
                            Reason = "Executable command metadata is required."
                        }
                )

        let finish input dryRun format all language _ details confirmation =
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
                    Details = details
                    Language = language
                })

        prepareWith false admit finish newId arguments

    /// Offline intent retains optional metadata and exposes no delivery invocation.
    let prepareOffline arguments =
        let mutation =
            CommandGrammar.tryOperation arguments
            |> Option.exists (fun operation -> (TaskRouting.act operation).IsSome)

        if not mutation || not (List.contains "--dry-run" arguments) then
            Ok None
        else
            let admit endpoint =
                function
                | Choice2Of2 intent -> Ok(endpoint, intent)
                | Choice1Of2 _ ->
                    Error
                        {
                            Path = "$"
                            Reason = "An operation draft is required."
                        }

            let finish (endpoint, intent) _ format _ language _ _ _ =
                Ok
                    {
                        Bytes =
                            JsonValue.Object
                                [
                                    "endpoint", JsonValue.String endpoint
                                    "input", TaskArguments.write intent
                                ]
                            |> WireWriter.bytes
                        Language = language
                        Format = format
                        Issues =
                            match OperationSubmission.syntaxDraft intent with
                            | Ok issues -> issues
                            | Error(SubmissionProblem.Rejected(Refusal.InvalidInput issues)) ->
                                Some issues
                            | Error _ ->
                                invalidOp "Declared argv must assemble a valid submission shape."
                    }

            prepareWith
                true
                admit
                finish
                (fun () -> invalidOp "An offline draft never allocates an operation ID.")
                arguments
            |> Result.map Some

    /// Interactive acquisition retains incomplete metadata until the current read supplies it.
    let internal draft arguments =
        let admit _ =
            function
            | Choice2Of2 draft -> Ok draft
            | _ ->
                Error
                    {
                        Path = "$"
                        Reason = "An operation draft is required."
                    }

        let finish input _ _ _ language preview _ _ = Ok(input, language, preview)

        prepareWith
            true
            admit
            finish
            (fun () -> invalidOp "A draft never allocates an operation ID.")
            arguments
