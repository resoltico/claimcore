namespace ClaimCore.Docs

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open Markdig.Renderers.Html
open Markdig.Syntax
open Markdig.Syntax.Inlines

[<RequireQualifiedAccess>]
module Links =
    let private explicitAnchor =
        Regex("^<a id=\"([a-z0-9]+(?:-[a-z0-9]+)*)\"></a>$", RegexOptions.CultureInvariant)

    let private validPercentEncoding (value: string) =
        let mutable valid = true
        let mutable index = 0

        while valid && index < value.Length do
            if value[index] = '%' then
                valid <-
                    index + 2 < value.Length
                    && Uri.IsHexDigit(value[index + 1])
                    && Uri.IsHexDigit(value[index + 2])

                index <- index + 3
            else
                index <- index + 1

        valid

    let private htmlText (document: MarkdownFile) (node: MarkdownObject) =
        MarkdownModel.sourceText document node

    let private addAnchor
        (document: MarkdownFile)
        (values: Dictionary<string, int>)
        (errors: ResizeArray<Diagnostic>)
        (id: string)
        (line: int)
        =
        match values.TryGetValue(id) with
        | true, _ ->
            errors.Add(
                Diagnostic.at
                    document.RelativePath
                    line
                    DiagnosticCode.InvalidLink
                    $"Anchor '{id}' is duplicated."
            )
        | _ -> values.Add(id, line)

    let private blockAnchors
        (document: MarkdownFile)
        (values: Dictionary<string, int>)
        (errors: ResizeArray<Diagnostic>)
        (ranges: ResizeArray<int * int>)
        =
        let add = addAnchor document values errors

        for node: Block in MarkdownModel.blocks document do
            let source = htmlText document node
            let matched = explicitAnchor.Match(source)

            if matched.Success then
                add matched.Groups[1].Value (MarkdownModel.sourceLine document node)
                ranges.Add(node.Span.Start, node.Span.End)
            else
                match node with
                | :? HeadingBlock as heading ->
                    let attributes = heading.GetAttributes()

                    match attributes.Id |> Option.ofObj with
                    | Some id when not (String.IsNullOrWhiteSpace(id)) ->
                        add id (MarkdownModel.sourceLine document heading)
                    | _ -> ()
                | :? HtmlBlock as html when
                    source.StartsWith("<a", StringComparison.OrdinalIgnoreCase)
                    ->
                    errors.Add(
                        Diagnostic.at
                            document.RelativePath
                            (MarkdownModel.sourceLine document html)
                            DiagnosticCode.InvalidLink
                            "Explicit anchors must use the exact supported syntax."
                    )
                | _ -> ()

    let private inlineAnchorErrors
        (document: MarkdownFile)
        (ranges: ResizeArray<int * int>)
        (errors: ResizeArray<Diagnostic>)
        =
        let lineIsAnchor (html: HtmlInline) =
            let before = document.Text.LastIndexOf('\n', max 0 (html.Span.Start - 1))
            let after = document.Text.IndexOf('\n', html.Span.End)
            let first = if before < 0 then 0 else before + 1
            let last = if after < 0 then document.Text.Length else after
            explicitAnchor.IsMatch(document.Text.Substring(first, last - first))

        for node: Inline in MarkdownModel.inlines document do
            match node with
            | :? HtmlInline as html when
                html.Tag.StartsWith("<a", StringComparison.OrdinalIgnoreCase)
                ->
                let allowed =
                    ranges
                    |> Seq.exists (fun (first, last) ->
                        html.Span.Start >= first && html.Span.End <= last)

                if not allowed && not (lineIsAnchor html) then
                    errors.Add(
                        Diagnostic.at
                            document.RelativePath
                            (MarkdownModel.sourceLine document html)
                            DiagnosticCode.InvalidLink
                            "Raw HTML links and inline anchors are not supported."
                    )
            | _ -> ()

    let anchors (document: MarkdownFile) =
        let values = Dictionary<string, int>(StringComparer.Ordinal)
        let errors = ResizeArray<Diagnostic>()
        let ranges = ResizeArray<int * int>()
        blockAnchors document values errors ranges
        inlineAnchorErrors document ranges errors

        if errors.Count = 0 then
            Ok(values.Keys |> Seq.toList |> Set.ofList)
        else
            Error(List.ofSeq errors)

    let private splitDestination (value: string) =
        let hash = value.IndexOf('#')

        let beforeFragment, fragment =
            if hash < 0 then
                value, None
            else
                value.Substring(0, hash), Some(value.Substring(hash + 1))

        let query = beforeFragment.IndexOf('?')

        let path =
            if query < 0 then
                beforeFragment
            else
                beforeFragment.Substring(0, query)

        path, fragment

    let private decode (part: string) =
        if validPercentEncoding part then
            try
                let value = Uri.UnescapeDataString(part)

                if value.Contains(char 0) || value.Contains(char 92) then
                    Error "A link contains an unsafe escaped character."
                else
                    Ok value
            with _ ->
                Error "A link contains malformed percent encoding."
        else
            Error "A link contains malformed percent encoding."

    let private externalDestination (value: string) =
        if value.StartsWith("//", StringComparison.Ordinal) then
            Error "Protocol-relative links are not allowed."
        else
            match Uri.TryCreate(value, UriKind.Absolute) with
            | true, possible ->
                match possible |> Option.ofObj with
                | Some uri when uri.Scheme = Uri.UriSchemeHttp || uri.Scheme = Uri.UriSchemeHttps ->
                    Ok true
                | Some uri when uri.Scheme = Uri.UriSchemeMailto -> Ok true
                | _ -> Error "The link uses an unsupported URI scheme."
            | _ -> Ok false

    let private validateFragment
        (anchorsByPath: Dictionary<string, Set<string>>)
        (target: string)
        (fragment: string option)
        =
        match fragment with
        | None -> Ok()
        | Some _ when Directory.Exists(target) ->
            Error "A directory link may not include a fragment."
        | Some value ->
            match anchorsByPath.TryGetValue(target) with
            | true, anchors when anchors.Contains(value) -> Ok()
            | true, _ -> Error $"Fragment '#{value}' does not exist in the target."
            | _ -> Error "A fragment target must be a checked Markdown document."

    let private validateLocal
        (root: RepositoryRoot)
        (anchorsByPath: Dictionary<string, Set<string>>)
        (document: MarkdownFile)
        (destination: string)
        =
        let rawPath, rawFragment = splitDestination destination
        let fragment = rawFragment |> Option.map decode |> Option.defaultValue (Ok "")

        match decode rawPath, fragment with
        | Error message, _
        | _, Error message -> Error message
        | Ok decodedPath, Ok decodedFragment ->
            let target =
                if decodedPath = "" then
                    Ok document.FullPath
                else
                    let directory =
                        Path.GetDirectoryName(document.FullPath)
                        |> Option.ofObj
                        |> Option.defaultValue root.Path

                    Repository.localPath root directory decodedPath

            match target with
            | Error message -> Error message
            | Ok path ->
                validateFragment
                    anchorsByPath
                    path
                    (rawFragment |> Option.map (fun _ -> decodedFragment))

    let private validateLink
        (root: RepositoryRoot)
        (anchorsByPath: Dictionary<string, Set<string>>)
        (document: MarkdownFile)
        (link: LinkInline)
        =
        let destination = link.Url |> Option.ofObj |> Option.defaultValue ""

        match externalDestination destination with
        | Error message -> Error message
        | Ok true -> Ok()
        | Ok false -> validateLocal root anchorsByPath document destination

    let check (root: RepositoryRoot) (documents: MarkdownFile list) =
        let anchorsByPath = Dictionary<string, Set<string>>()
        let errors = ResizeArray<Diagnostic>()

        for document in documents do
            match anchors document with
            | Ok found -> anchorsByPath.Add(document.FullPath, found)
            | Error found -> found |> List.iter errors.Add

        let links =
            documents
            |> List.collect (fun document ->
                MarkdownModel.inlines document
                |> Seq.choose (function
                    | :? LinkInline as link when
                        link.Url
                        |> Option.ofObj
                        |> Option.exists (String.IsNullOrWhiteSpace >> not)
                        ->
                        Some(document, link)
                    | _ -> None)
                |> Seq.toList)

        for document, link in links do
            match validateLink root anchorsByPath document link with
            | Ok() -> ()
            | Error message ->
                errors.Add(
                    Diagnostic.at
                        document.RelativePath
                        (MarkdownModel.sourceLine document link)
                        DiagnosticCode.InvalidLink
                        message
                )

        if errors.Count = 0 then
            Ok links.Length
        else
            Error(List.ofSeq errors)
