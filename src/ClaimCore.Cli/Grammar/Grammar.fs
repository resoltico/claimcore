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
        Rendering: RenderingContext
        Format: VerbFormat
        Issues: InputIssues option
    }

    member this.Language = this.Rendering.Language

[<NoEquality; NoComparison>]
type PreparedVerb =
    {
        Invocation: Invocation
        DryRun: bool
        Confirmation: string option
        Format: VerbFormat
        All: bool
        Details: bool
        Rendering: RenderingContext
    }

    member this.Language = this.Rendering.Language
    member this.Bytes = Frames.encodeInvocation this.Invocation

module Grammar =
    open VerbOptions

    let private errorKey =
        function
        | UsageError.UnknownOption(flag, _)
        | UsageError.MissingOption flag
        | UsageError.InvalidInteger(flag, _)
        | UsageError.InvalidRange(flag, _, _, _)
        | UsageError.InvalidUuid flag
        | UsageError.InvalidBoolean flag
        | UsageError.InvalidCollection(flag, _)
        | UsageError.InvalidCollectionCount(flag, _, _)
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
        | UsageError.InvalidInteger _
        | UsageError.InvalidRange _
        | UsageError.InvalidUuid _
        | UsageError.InvalidBoolean _
        | UsageError.InvalidCollection _
        | UsageError.InvalidCollectionCount _ -> 3
        | UsageError.InvalidValue _ -> 2
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
        (entry: string)
        source
        options
        operands
        (problems: ResizeArray<UsageError>)
        =
        match (TaskRouting.act entry) with
        | _ when problems.Count > 0 -> Choice1Of2 None
        | Some kind ->
            let intent =
                TaskArguments.mutation incomplete allocate kind source options operands problems

            Choice2Of2 intent
        | None ->
            match (TaskRouting.endpoint entry) with
            | ActorEndpoint.Query _
            | ActorEndpoint.Command _ ->
                QueryArguments.build entry options operands problems |> Choice1Of2
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
            let syntax = CommandGrammar.optionGrammar entry

            let options, operands =
                tokenize
                    syntax.Known
                    syntax.Available
                    syntax.Switches
                    syntax.Repeated
                    arguments
                    problems

            let source, sourceIssue =
                if not (CommandGrammar.uploadsFile entry) || problems.Count > 0 then
                    None, None
                else
                    match options.TryGetValue "--file" with
                    | true, [ Some path ] ->
                        match AttachmentFiles.capture path with
                        | Ok source -> Some source, None
                        | Error(AttachmentCaptureFailure.TooLarge actual) ->
                            None,
                            Some(
                                UsageError.AttachmentFileTooLarge(
                                    AttachmentLimits.FileBytes,
                                    actual
                                )
                            )
                        | Error _ -> None, Some(UsageError.InvalidValue("--file", ""))
                    | _ -> None, Some(UsageError.MissingOption "--file")

            let body =
                build
                    (incomplete || options.ContainsKey "--dry-run")
                    newId
                    entry
                    source
                    options
                    operands
                    problems

            sourceIssue |> Option.iter problems.Add

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

            let displayLocale =
                option
                    "--display-locale"
                    (LocalizationPolicy.languageOption (
                        if List.contains language LocalizationPolicy.languages then
                            language
                        else
                            "en"
                    ))
                        .displayLocale

            let rendering = RenderingContext.create language displayLocale

            if not (List.contains displayLocale LocalizationPolicy.displayLocales) then
                problems.Add(
                    UsageError.InvalidChoice(
                        "--display-locale",
                        displayLocale,
                        LocalizationPolicy.displayLocales
                    )
                )

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
                && not (TaskRouting.requiresExactConfirmation entry)
                && not (CommandGrammar.attachmentTransfer entry)
            then
                problems.Add(
                    UsageError.NotApplicable(
                        "--confirm",
                        String.concat " " (CommandGrammar.words entry)
                    )
                )

            if CommandGrammar.attachmentTransfer entry then
                match options.TryGetValue "--confirm" with
                | true, [ Some value ] when value <> "yes" ->
                    problems.Add(UsageError.InvalidChoice("--confirm", value, [ "yes" ]))
                | _ -> ()

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
                        (rendering |> Option.get)
                        (options.ContainsKey "--preview")
                        (options.ContainsKey "--details")
                        (source |> Option.map _.Bytes)
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

        let finish input dryRun format all language preview details upload confirmation =
            Frames.fromInput input
            |> Result.mapError (fun _ -> [ UsageError.RequestTooLarge Limits.MaximumRequestBytes ])
            |> Result.map (fun invocation ->
                {
                    Invocation =
                        if
                            (match (Frames.endpoint invocation).Id with
                             | EndpointId.Actor(ActorEndpoint.Operation _) -> false
                             | _ -> true)
                            || (VerbOptions.beforeTerminator arguments
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
                        |> Frames.withUpload (if preview then None else upload)
                    DryRun = dryRun
                    Confirmation = confirmation
                    Format = format
                    All = all
                    Details = details
                    Rendering = language
                })

        prepareWith false admit finish newId arguments

    /// Offline intent retains optional metadata and exposes no delivery invocation.
    let prepareOffline arguments =
        let mutation =
            CommandGrammar.tryOperation arguments
            |> Option.exists (fun operation -> (TaskRouting.act operation).IsSome)

        if not mutation || not (VerbOptions.hasOption "--dry-run" arguments) then
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

            let finish (endpoint, intent) _ format _ language _ _ _ _ =
                Ok
                    {
                        Bytes =
                            JsonValue.Object
                                [
                                    "endpoint", JsonValue.String endpoint
                                    "input", TaskArguments.write intent
                                ]
                            |> WireWriter.bytes
                        Rendering = language
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

        let finish input _ _ _ language preview _ upload _ = Ok(input, language, preview, upload)

        prepareWith
            true
            admit
            finish
            (fun () -> invalidOp "A draft never allocates an operation ID.")
            arguments

    let internal prepareAttachmentDownload operation arguments =
        let flags = (CommandGrammar.optionGrammar operation).Available
        let problems = ResizeArray<UsageError>()

        let options, operands =
            VerbOptions.tokenize
                flags
                flags
                []
                []
                (List.skip (CommandGrammar.words operation).Length arguments)
                problems

        let option name =
            match options.TryGetValue name with
            | true, [ Some value ] -> Some value
            | _ -> None

        let language = option "--language" |> Option.defaultValue "en"

        let display =
            option "--display-locale"
            |> Option.defaultValue
                (LocalizationPolicy.languageOption (
                    if List.contains language LocalizationPolicy.languages then
                        language
                    else
                        "en"
                ))
                    .displayLocale

        let rendering = RenderingContext.create language display

        if not (List.contains language LocalizationPolicy.languages) then
            problems.Add(
                UsageError.InvalidChoice("--language", language, LocalizationPolicy.languages)
            )

        if not (List.contains display LocalizationPolicy.displayLocales) then
            problems.Add(
                UsageError.InvalidChoice(
                    "--display-locale",
                    display,
                    LocalizationPolicy.displayLocales
                )
            )

        let selection =
            match option "--case-id", operands with
            | Some raw, [ attachment ] ->
                match VerbOptions.uuid "--case-id" raw with
                | Ok caseId -> Some(CaseSelector.Id(CaseId.create caseId |> Option.get), attachment)
                | Error problem ->
                    problems.Add problem
                    None
            | None, [ reference; attachment ] -> Some(CaseSelector.Reference reference, attachment)
            | _ ->
                problems.Add(UsageError.MissingOperand "CASE ATTACHMENT-ID")
                None

        let selection =
            selection
            |> Option.bind (fun (target, attachment) ->
                match VerbOptions.uuid "ATTACHMENT-ID" attachment with
                | Ok id ->
                    Some
                        {
                            AttachmentReadRequest.Target = target
                            AttachmentId = AttachmentId.create id |> Option.get
                        }
                | Error problem ->
                    problems.Add problem
                    None)

        match selection, option "--output", rendering with
        | Some selection, Some path, Some rendering when problems.Count = 0 ->
            Ok(selection, path, rendering)
        | _, destination, _ ->
            if destination.IsNone && not (options.ContainsKey "--output") then
                problems.Add(UsageError.MissingOption "--output")

            Error(List.ofSeq problems)
