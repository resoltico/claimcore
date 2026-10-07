namespace ClaimCore.Docs

[<RequireQualifiedAccess>]
module Generators =
    let private registration id document product =
        {
            Id = id
            Document = document
            Render = ExecutableHelp.render product
        }

    let registrations =
        [
            {
                Id = "quality-stages"
                Document = "docs/development.md"
                Render =
                    fun context -> DevelopmentBlocks.render context.Root context.Processes "stages"
            }
            {
                Id = "pinned-tools"
                Document = "docs/development.md"
                Render =
                    fun context -> DevelopmentBlocks.render context.Root context.Processes "tools"
            }
            {
                Id = "contract-tests"
                Document = "docs/contract-tests.md"
                Render =
                    fun context ->
                        ContractTokens.render context.Root
                        |> Result.mapError (fun message ->
                            [ Diagnostic.create DiagnosticCode.InvalidContract message ])
            }
            registration "cli-help" "docs/cli.md" "ClaimCore.Cli"
            registration "database-help" "docs/database.md" "ClaimCore.Database"
            registration "web-help" "docs/web.md" "ClaimCore.Web"
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
            match ExecutableHelp.build context with
            | Error found -> Error found
            | Ok() ->
                let outputs = ResizeArray<GeneratedDocument>()

                documents
                |> List.choose (generateDocument context blocks errors)
                |> List.iter outputs.Add

                if errors.Count = 0 then
                    Ok(List.ofSeq outputs)
                else
                    Error(List.ofSeq errors)
