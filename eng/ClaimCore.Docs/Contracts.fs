namespace ClaimCore.Docs

open System
open System.Collections.Generic
open System.Text.RegularExpressions
open Markdig.Syntax

[<NoEquality; NoComparison>]
type ContractDeclaration =
    {
        Id: string
        Document: string
        Line: int
        SectionBytes: byte array
    }

[<RequireQualifiedAccess>]
module Contracts =
    let private headingPattern =
        Regex("^#{2,4} (CC-[A-Z]{2,12}-[0-9]{3}) (?:—|-) [^\r\n]+$", RegexOptions.CultureInvariant)

    let private expectedAnchor (id: string) =
        $"<a id=\"{id.ToLowerInvariant()}\"></a>"

    let private contractAnchorPattern =
        Regex("^<a id=\"cc-[a-z]{2,12}-[0-9]{3}\"></a>$", RegexOptions.CultureInvariant)

    let private anchor (document: MarkdownFile) (id: string) (heading: HeadingBlock) =
        let previous =
            MarkdownModel.blocks document
            |> Seq.filter (fun candidate -> candidate.Span.End < heading.Span.Start)
            |> Seq.sortByDescending _.Span.End
            |> Seq.tryHead

        match previous with
        | None -> Error $"Contract '{id}' has no explicit anchor."
        | Some candidate ->
            let gapStart = candidate.Span.End + 1
            let gapLength = heading.Span.Start - gapStart

            let gap =
                if gapLength < 0 then
                    ""
                else
                    document.Text.Substring(gapStart, gapLength)

            let anchorText = MarkdownModel.sourceText document candidate

            if anchorText = expectedAnchor id && gap = "\n" then
                Ok candidate
            else
                Error $"Contract '{id}' must immediately follow its matching explicit anchor."

    let private sectionBytes (document: MarkdownFile) (anchorNode: Block) (heading: HeadingBlock) =
        let nextHeading =
            MarkdownModel.blocks document
            |> Seq.choose (function
                | :? HeadingBlock as candidate when
                    candidate.Span.Start > heading.Span.Start && candidate.Level <= heading.Level
                    ->
                    Some candidate
                | _ -> None)
            |> Seq.sortBy _.Span.Start
            |> Seq.tryHead

        let finish =
            match nextHeading with
            | None -> document.Text.Length
            | Some next ->
                MarkdownModel.blocks document
                |> Seq.filter (fun candidate -> candidate.Span.End < next.Span.Start)
                |> Seq.sortByDescending _.Span.End
                |> Seq.tryHead
                |> Option.filter (fun candidate ->
                    MarkdownModel.sourceText document candidate |> contractAnchorPattern.IsMatch)
                |> Option.map _.Span.Start
                |> Option.defaultValue next.Span.Start

        document.Text.Substring(anchorNode.Span.Start, finish - anchorNode.Span.Start)
        |> Text.Encoding.UTF8.GetBytes

    let private inspectHeading
        (document: MarkdownFile)
        (ids: HashSet<string>)
        (heading: HeadingBlock)
        =
        let source = MarkdownModel.sourceText document heading
        let matched = headingPattern.Match(source)

        if not matched.Success then
            None, []
        else
            let id = matched.Groups[1].Value
            let line = MarkdownModel.sourceLine document heading
            let diagnostics = ResizeArray<Diagnostic>()

            if not (ids.Add(id)) then
                diagnostics.Add(
                    Diagnostic.at
                        document.RelativePath
                        line
                        DiagnosticCode.InvalidContract
                        $"Contract ID '{id}' is duplicated."
                )

            let declaration =
                match anchor document id heading with
                | Error message ->
                    diagnostics.Add(
                        Diagnostic.at
                            document.RelativePath
                            line
                            DiagnosticCode.InvalidContract
                            message
                    )

                    None
                | Ok anchorNode ->
                    Some
                        {
                            Id = id
                            Document = document.RelativePath
                            Line = line
                            SectionBytes = sectionBytes document anchorNode heading
                        }

            declaration, List.ofSeq diagnostics

    let declarations (documents: MarkdownFile list) =
        let found = ResizeArray<ContractDeclaration>()
        let errors = ResizeArray<Diagnostic>()
        let ids = HashSet<string>(StringComparer.Ordinal)

        for document in documents do
            for node: Block in MarkdownModel.blocks document do
                match node with
                | :? HeadingBlock as heading ->
                    let declaration, diagnostics = inspectHeading document ids heading
                    declaration |> Option.iter found.Add
                    diagnostics |> List.iter errors.Add
                | _ -> ()

        if found.Count = 0 then
            errors.Add(
                Diagnostic.create
                    DiagnosticCode.InvalidContract
                    "No contract headings were found; documentation evidence cannot pass vacuously."
            )

        if errors.Count = 0 then
            Ok(found |> Seq.sortBy _.Id |> Seq.toList)
        else
            Error(List.ofSeq errors)
