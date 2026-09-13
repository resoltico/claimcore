namespace ClaimCore.Docs

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open Markdig
open Markdig.Syntax
open Markdig.Syntax.Inlines

[<RequireQualifiedAccess>]
module MarkdownModel =
    let private utf8 = UTF8Encoding(false, true)

    let pipeline = MarkdownPipeline.value

    let private lineNumber (text: string) offset =
        let mutable line = 1

        for index in 0 .. min offset text.Length - 1 do
            if text[index] = '\n' then
                line <- line + 1

        line

    let read (root: RepositoryRoot) path =
        match Repository.ensureExistingSafe root path with
        | Error message -> Error(Diagnostic.create DiagnosticCode.UnsafePath message)
        | Ok safePath ->
            try
                let bytes = File.ReadAllBytes(safePath)

                if bytes.Length >= 3 && bytes[0..2] = [| 0xEFuy; 0xBBuy; 0xBFuy |] then
                    Error(
                        Diagnostic.create
                            DiagnosticCode.InvalidMarkdown
                            "Repository Markdown must use UTF-8 without a byte-order mark."
                    )
                else
                    let text = utf8.GetString(bytes)

                    if text.Contains("\r") then
                        Error(
                            Diagnostic.create
                                DiagnosticCode.InvalidMarkdown
                                "Repository Markdown must use LF line endings."
                        )
                    elif not (text.EndsWith('\n')) then
                        Error(
                            Diagnostic.create
                                DiagnosticCode.InvalidMarkdown
                                "Repository Markdown must end with one LF."
                        )
                    else
                        Ok
                            {
                                RelativePath = Repository.relativePath root safePath
                                FullPath = safePath
                                Bytes = bytes
                                Text = text
                                Sha256 = Repository.sha256Bytes bytes
                                Document = Markdown.Parse(text, pipeline)
                            }
            with error ->
                Error(
                    Diagnostic.create
                        DiagnosticCode.InvalidMarkdown
                        (error.GetType().Name + ": " + error.Message)
                )

    let internal fromText (relativePath: string) (fullPath: string) (text: string) =
        let bytes = utf8.GetBytes(text)

        {
            RelativePath = relativePath
            FullPath = fullPath
            Bytes = bytes
            Text = text
            Sha256 = Repository.sha256Bytes bytes
            Document = Markdown.Parse(text, pipeline)
        }

    let readAll root =
        try
            let mutable errors = []
            let mutable documents = []

            for path in Repository.markdownFiles root do
                match read root path with
                | Ok document -> documents <- document :: documents
                | Error error -> errors <- error :: errors

            if errors.IsEmpty then
                Ok(documents |> List.sortBy _.RelativePath)
            else
                Error(List.rev errors)
        with error ->
            Error [ Diagnostic.create DiagnosticCode.UnsafePath error.Message ]

    let private nodeText (source: string) (node: MarkdownObject) =
        if node.Span.Start < 0 || node.Span.End < node.Span.Start then
            ""
        else
            source.Substring(node.Span.Start, node.Span.End - node.Span.Start + 1)

    let blocks (document: MarkdownFile) : Block seq = document.Document.Descendants()

    let inlines (document: MarkdownFile) =
        let rec walk (item: Inline) =
            seq {
                yield item

                match item with
                | :? ContainerInline as container ->
                    match container.FirstChild |> Option.ofObj with
                    | Some child -> yield! siblings child
                    | None -> ()
                | _ -> ()
            }

        and siblings (item: Inline) =
            seq {
                yield! walk item

                match item.NextSibling |> Option.ofObj with
                | Some next -> yield! siblings next
                | None -> ()
            }

        blocks document
        |> Seq.choose (fun (item: Block) ->
            match item with
            | :? LeafBlock as leaf -> leaf.Inline |> Option.ofObj
            | _ -> None)
        |> Seq.collect siblings

    let private markerPattern =
        Regex("^<!-- generated:(begin|end) ([a-z][a-z0-9-]*) -->$", RegexOptions.CultureInvariant)

    let private markerEvents (document: MarkdownFile) =
        let events = ResizeArray<string * string * MarkdownObject>()
        let errors = ResizeArray<Diagnostic>()

        for node: Block in blocks document do
            match node with
            | :? HtmlBlock as html ->
                let text = nodeText document.Text html

                if text.Contains("generated:", StringComparison.Ordinal) then
                    let matched = markerPattern.Match(text)

                    if matched.Success then
                        events.Add(matched.Groups[1].Value, matched.Groups[2].Value, html)
                    else
                        errors.Add(
                            Diagnostic.at
                                document.RelativePath
                                (lineNumber document.Text html.Span.Start)
                                DiagnosticCode.InvalidMarkdown
                                "Generated markers must use the exact standalone marker syntax."
                        )
            | _ -> ()

        for node: Inline in inlines document do
            match node with
            | :? HtmlInline as html when html.Tag.Contains("generated:", StringComparison.Ordinal) ->
                errors.Add(
                    Diagnostic.at
                        document.RelativePath
                        (lineNumber document.Text html.Span.Start)
                        DiagnosticCode.InvalidMarkdown
                        "Generated markers may not be inline."
                )
            | _ -> ()

        events, errors

    let private closeBlock
        (document: MarkdownFile)
        (id: string)
        (beginNode: MarkdownObject)
        (endNode: MarkdownObject)
        =
        let bodyStart = beginNode.Span.End + 1
        let bodyEnd = endNode.Span.Start

        if
            bodyStart >= document.Text.Length
            || document.Text[bodyStart] <> '\n'
            || bodyEnd <= bodyStart
        then
            Error(
                Diagnostic.at
                    document.RelativePath
                    (lineNumber document.Text beginNode.Span.Start)
                    DiagnosticCode.InvalidMarkdown
                    "Generated marker lines must surround a newline-terminated body."
            )
        else
            Ok
                {
                    Id = id
                    RelativePath = document.RelativePath
                    BeginStart = beginNode.Span.Start
                    BodyStart = bodyStart + 1
                    BodyEnd = bodyEnd
                    EndFinish = endNode.Span.End + 1
                    BeginLine = lineNumber document.Text beginNode.Span.Start
                }

    let private pairMarkers
        (document: MarkdownFile)
        (events: ResizeArray<string * string * MarkdownObject>)
        (errors: ResizeArray<Diagnostic>)
        =
        let found = ResizeArray<GeneratedBlock>()
        let mutable opened: (string * MarkdownObject) option = None

        let addError (node: MarkdownObject) (message: string) =
            errors.Add(
                Diagnostic.at
                    document.RelativePath
                    (lineNumber document.Text node.Span.Start)
                    DiagnosticCode.InvalidMarkdown
                    message
            )

        for kind, id, node in events do
            match kind, opened with
            | "begin", None -> opened <- Some(id, node)
            | "begin", Some _ -> addError node "Generated blocks may not be nested."
            | "end", None -> addError node "Generated block has an orphaned end marker."
            | "end", Some(openId, _) when openId <> id ->
                addError node $"Generated block '{openId}' ends as '{id}'."
                opened <- None
            | "end", Some(_, beginNode) ->
                match closeBlock document id beginNode node with
                | Ok block -> found.Add(block)
                | Error error -> errors.Add(error)

                opened <- None
            | _ -> ()

        match opened with
        | Some(id, node) -> addError node $"Generated block '{id}' has no end marker."
        | None -> ()

        found

    let generatedBlocks (document: MarkdownFile) =
        let events, errors = markerEvents document
        let found = pairMarkers document events errors

        if errors.Count = 0 then
            Ok(List.ofSeq found)
        else
            Error(List.ofSeq errors)

    let body (document: MarkdownFile) (block: GeneratedBlock) =
        document.Text.Substring(block.BodyStart, block.BodyEnd - block.BodyStart)

    let replaceBodies (document: MarkdownFile) (replacements: (GeneratedBlock * string) list) =
        let ordered =
            replacements
            |> List.sortByDescending (fun (block: GeneratedBlock, _) -> block.BodyStart)

        let mutable text = document.Text

        for block, replacement in ordered do
            text <- text.Remove(block.BodyStart, block.BodyEnd - block.BodyStart)
            text <- text.Insert(block.BodyStart, replacement)

        text

    let sourceText (document: MarkdownFile) (node: MarkdownObject) = nodeText document.Text node

    let sourceLine (document: MarkdownFile) (node: MarkdownObject) =
        lineNumber document.Text node.Span.Start
