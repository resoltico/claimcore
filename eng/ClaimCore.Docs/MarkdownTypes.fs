namespace ClaimCore.Docs

open Markdig.Syntax

[<NoEquality; NoComparison>]
type MarkdownFile =
    {
        RelativePath: string
        FullPath: string
        Bytes: byte array
        Text: string
        Sha256: string
        Document: MarkdownDocument
    }

[<NoEquality; NoComparison>]
type GeneratedBlock =
    {
        Id: string
        RelativePath: string
        BeginStart: int
        BodyStart: int
        BodyEnd: int
        EndFinish: int
        BeginLine: int
    }
