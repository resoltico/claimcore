namespace ClaimCore.Docs

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
