module ClaimCore.Tests.DocumentationChecks

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Markdig
open Markdig.Syntax
open Markdig.Syntax.Inlines
open Markdig.Renderers.Html
open Markdig.Extensions.AutoIdentifiers

let private pipeline =
    MarkdownPipelineBuilder().UseAutoIdentifiers(AutoIdentifierOptions.GitHub).Build()

let private anchors text =
    let parsed = Markdown.Parse(text, pipeline)

    let headings =
        parsed.Descendants<HeadingBlock>()
        |> Seq.map (fun heading -> heading.GetAttributes().Id |> nonNull)

    let html =
        Seq.append
            (parsed.Descendants<HtmlBlock>() |> Seq.map (fun block -> block.Lines.ToString()))
            (parsed.Descendants<HtmlInline>() |> Seq.map _.Tag)

    let explicit =
        html
        |> Seq.collect (fun tag ->
            Regex.Matches(tag, "\\bid\\s*=\\s*[\"']([^\"'<>]+)[\"']")
            |> Seq.map (fun found -> found.Groups[1].Value))

    Seq.append headings explicit |> Set.ofSeq

let links (documents: Map<string, string>) exists =
    [
        for KeyValue(path, text) in documents do
            let parsed = Markdown.Parse(text, pipeline)

            for link in parsed.Descendants<LinkInline>() do
                let url = link.Url |> Option.ofObj |> Option.defaultValue ""

                if
                    url <> ""
                    && not (Uri.IsWellFormedUriString(url, UriKind.Absolute))
                    && not (url.StartsWith("//", StringComparison.Ordinal))
                then
                    let parts = url.Split('#', 2)

                    let target =
                        if parts[0] = "" then
                            path
                        else
                            Path.GetFullPath(
                                Path.Combine(
                                    Path.GetDirectoryName(path) |> nonNull,
                                    Uri.UnescapeDataString parts[0]
                                )
                            )

                    if not (exists target) then
                        yield path + ": missing relative link " + url
                    elif parts.Length = 2 && parts[1] <> "" && documents.ContainsKey target then
                        let fragment = Uri.UnescapeDataString parts[1]

                        if not (Set.contains fragment (anchors documents[target])) then
                            yield path + ": missing heading link " + url
    ]

let private sourcePaths root =
    let start =
        ProcessStartInfo(
            "git",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    for argument in [ "ls-files"; "--cached"; "--others"; "--exclude-standard"; "-z" ] do
        start.ArgumentList.Add argument

    use child = Process.Start(start) |> nonNull
    let output = child.StandardOutput.ReadToEndAsync()
    let errors = child.StandardError.ReadToEndAsync()

    if not (child.WaitForExit 30000) then
        child.Kill true
        invalidOp "Documentation source discovery timed out."

    errors.GetAwaiter().GetResult() |> ignore

    if child.ExitCode <> 0 then
        invalidOp "Documentation source discovery failed."

    output.GetAwaiter().GetResult().Split('\u0000', StringSplitOptions.RemoveEmptyEntries)
    |> Array.map (fun path -> path, Path.GetFullPath(Path.Combine(root, path)))
    |> Array.filter (snd >> File.Exists)

let repository root =
    let paths = sourcePaths root

    let current (path: string) =
        path <> "CHANGELOG.md"
        && not (path.StartsWith("docs/decisions/", StringComparison.Ordinal))
        && not (path.EndsWith("-design.md", StringComparison.Ordinal))
        && not (path.EndsWith("-design-qa.md", StringComparison.Ordinal))

    let documents =
        paths
        |> Array.filter (fun (path, _) ->
            path.EndsWith(".md", StringComparison.Ordinal) && current path)
        |> Array.map (fun (_, path) -> path, File.ReadAllText path)
        |> Map.ofArray

    links documents (fun path -> File.Exists path || Directory.Exists path)
