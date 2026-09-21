namespace ClaimCore.Docs

open System

[<NoEquality; NoComparison>]
type GenerationContext =
    {
        Root: RepositoryRoot
        Processes: IProcessRunner
    }

[<NoEquality; NoComparison>]
type BlockRegistration =
    {
        Id: string
        Document: string
        Render: GenerationContext -> Result<string, Diagnostic list>
    }

[<NoEquality; NoComparison>]
type GeneratedDocument =
    {
        Original: MarkdownFile
        ExpectedText: string
        Blocks: BlockStatus list
    }

[<RequireQualifiedAccess>]
module Generators =
    let private invoke context arguments timeout =
        context.Processes.Run(
            {
                FileName = "dotnet"
                Arguments = arguments
                WorkingDirectory = context.Root.Path
                Environment =
                    [
                        "DOTNET_NOLOGO", Some "1"
                        "DOTNET_CLI_TELEMETRY_OPTOUT", Some "1"
                        "NO_COLOR", Some "1"
                        "TERM", Some "dumb"
                        "TZ", Some "UTC"
                        "CLAIMCORE_CONNECTION_FILE", None
                        "CLAIMCORE_ADMIN_CONNECTION_FILE", None
                        "CLAIMCORE_WEB_STATE_DIR", None
                        "CLAIMCORE_WEB_CERTIFICATE_PATH", None
                        "CLAIMCORE_WEB_ORIGIN", None
                        "CLAIMCORE_WEB_MAX_JSON_BYTES", None
                        "CLAIMCORE_WEB_CORE_PERMITS", None
                        "CLAIMCORE_WEB_CORE_QUEUE", None
                        "CLAIMCORE_WEB_LOGIN_PERMITS", None
                        "CLAIMCORE_WEB_SESSION_IDLE_MINUTES", None
                        "CLAIMCORE_WEB_SESSION_ABSOLUTE_MINUTES", None
                    ]
                Timeout = timeout
            }
        )
        |> Result.mapError (fun message -> [ Diagnostic.create DiagnosticCode.Invocation message ])

    let private requireSuccess purpose result =
        match result with
        | Error errors -> Error errors
        | Ok output when output.ExitCode <> 0 ->
            Error
                [
                    Diagnostic.create
                        DiagnosticCode.Invocation
                        $"{purpose} failed with exit code {output.ExitCode}."
                ]
        | Ok output -> Ok output

    let private validatedTarget context product (output: ProcessOutput) =
        let target = output.StandardOutput.Trim()

        match Repository.ensureExistingSafe context.Root target with
        | Error message -> Error [ Diagnostic.create DiagnosticCode.UnsafePath message ]
        | Ok safeTarget ->
            let relative = Repository.relativePath context.Root safeTarget
            let prefix = "artifacts/bin/" + product + "/"
            let suffix = "/" + product + ".dll"

            if
                not (relative.StartsWith(prefix, StringComparison.Ordinal))
                || not (relative.EndsWith(suffix, StringComparison.Ordinal))
            then
                Error
                    [
                        Diagnostic.create
                            DiagnosticCode.UnsafePath
                            $"The resolved {product} artifact is outside its registered artifact tree."
                    ]
            else
                Ok safeTarget

    let private resolveExecutable context product project =
        invoke
            context
            [ "build"; project; "--configuration"; "Release"; "--no-restore" ]
            (TimeSpan.FromMinutes(5.0))
        |> requireSuccess (product + " build")
        |> Result.bind (fun _ ->
            invoke
                context
                [
                    "msbuild"
                    project
                    "-nologo"
                    "-verbosity:quiet"
                    "-property:Configuration=Release"
                    "-getProperty:TargetPath"
                ]
                (TimeSpan.FromMinutes(1.0))
            |> requireSuccess (product + " target resolution"))
        |> Result.bind (validatedTarget context product)

    let private normalizedHelp output =
        if not (String.IsNullOrEmpty(output.StandardError)) then
            Error
                [
                    Diagnostic.create
                        DiagnosticCode.Invocation
                        "Executable help wrote to standard error."
                ]
        else
            let normalized = output.StandardOutput.Replace("\r\n", "\n")

            if normalized.Contains('\r') || normalized.Contains(char 0) then
                Error
                    [
                        Diagnostic.create
                            DiagnosticCode.Invocation
                            "Executable help contained unsupported control/newline data."
                    ]
            elif normalized.Contains("<!-- generated:", StringComparison.Ordinal) then
                Error
                    [
                        Diagnostic.create
                            DiagnosticCode.Invocation
                            "Executable help may not emit documentation marker text."
                    ]
            else
                let content = normalized.TrimEnd('\n') + "\n"
                Ok("```text\n" + content + "```\n")

    let private helpBlock product project context =
        resolveExecutable context product project
        |> Result.bind (fun target ->
            invoke context [ target; "help" ] (TimeSpan.FromMinutes(1.0))
            |> requireSuccess (product + " help"))
        |> Result.bind normalizedHelp

    let private registration id document product project =
        {
            Id = id
            Document = document
            Render = helpBlock product project
        }

    let registrations =
        [
            registration
                "cli-help"
                "docs/cli.md"
                "ClaimCore.Cli"
                "src/ClaimCore.Cli/ClaimCore.Cli.fsproj"
            registration
                "database-help"
                "docs/database.md"
                "ClaimCore.Database"
                "src/ClaimCore.Database/ClaimCore.Database.fsproj"
            registration
                "web-help"
                "docs/web.md"
                "ClaimCore.Web"
                "src/ClaimCore.Web/ClaimCore.Web.fsproj"
            {
                Id = "architecture-components"
                Document = "docs/architecture.md"
                Render =
                    fun context ->
                        ArchitectureTable.render context.Root
                        |> Result.mapError (fun message ->
                            [ Diagnostic.create DiagnosticCode.InvalidManifest message ])
            }
        ]

    let private collectBlocks (documents: MarkdownFile list) (errors: ResizeArray<Diagnostic>) =
        let blocks = ResizeArray<MarkdownFile * GeneratedBlock>()

        for document in documents do
            match MarkdownModel.generatedBlocks document with
            | Ok found -> found |> List.iter (fun block -> blocks.Add(document, block))
            | Error found -> found |> List.iter errors.Add

        blocks

    let private validateBlocks
        (blocks: ResizeArray<MarkdownFile * GeneratedBlock>)
        (errors: ResizeArray<Diagnostic>)
        =
        for document, block in blocks do
            match registrations |> List.tryFind (fun item -> item.Id = block.Id) with
            | None ->
                errors.Add(
                    Diagnostic.at
                        document.RelativePath
                        block.BeginLine
                        DiagnosticCode.InvalidMarkdown
                        $"Generated block '{block.Id}' is not registered."
                )
            | Some registration when registration.Document <> document.RelativePath ->
                errors.Add(
                    Diagnostic.at
                        document.RelativePath
                        block.BeginLine
                        DiagnosticCode.InvalidMarkdown
                        $"Generated block '{block.Id}' is in the wrong document."
                )
            | _ -> ()

        for registration in registrations do
            let count =
                blocks
                |> Seq.filter (fun (_, block) -> block.Id = registration.Id)
                |> Seq.length

            if count <> 1 then
                errors.Add(
                    Diagnostic.create
                        DiagnosticCode.InvalidMarkdown
                        $"Registered block '{registration.Id}' occurs {count} times; expected exactly once."
                )

    let private generateDocument
        (context: GenerationContext)
        (blocks: ResizeArray<MarkdownFile * GeneratedBlock>)
        (errors: ResizeArray<Diagnostic>)
        (document: MarkdownFile)
        =
        let replacements = ResizeArray<GeneratedBlock * string>()
        let statuses = ResizeArray<BlockStatus>()

        for candidate, block in blocks do
            if candidate.RelativePath = document.RelativePath then
                let registration = registrations |> List.find (fun item -> item.Id = block.Id)

                match registration.Render context with
                | Error found -> found |> List.iter errors.Add
                | Ok expected ->
                    replacements.Add(block, expected)

                    statuses.Add(
                        {
                            Id = block.Id
                            Document = document.RelativePath
                            ExpectedSha256 = Repository.sha256Text expected
                            Status =
                                if MarkdownModel.body document block = expected then
                                    "matched"
                                else
                                    "changed"
                        }
                    )

        if replacements.Count = 0 then
            None
        else
            Some
                {
                    Original = document
                    ExpectedText = MarkdownModel.replaceBodies document (List.ofSeq replacements)
                    Blocks = List.ofSeq statuses
                }

    let generate (context: GenerationContext) (documents: MarkdownFile list) =
        let errors = ResizeArray<Diagnostic>()
        let blocks = collectBlocks documents errors
        validateBlocks blocks errors

        if errors.Count > 0 then
            Error(List.ofSeq errors)
        else
            let outputs = ResizeArray<GeneratedDocument>()

            documents
            |> List.choose (generateDocument context blocks errors)
            |> List.iter outputs.Add

            if errors.Count = 0 then
                Ok(List.ofSeq outputs)
            else
                Error(List.ofSeq errors)
