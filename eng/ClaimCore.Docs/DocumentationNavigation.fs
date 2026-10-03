namespace ClaimCore.Docs

open System.Collections.Generic

/// Only links reachable from the documentation map make a document discoverable.
module internal DocumentationNavigation =
    let check (documents: MarkdownFile list) (edges: (string * string) list) =
        let entry = "docs/README.md"

        let required =
            documents |> List.filter (fun item -> item.RelativePath.StartsWith("docs/"))

        let reached = HashSet<string>()
        let pending = Queue<string>()
        reached.Add(entry) |> ignore
        pending.Enqueue(entry)

        while pending.Count > 0 do
            let source = pending.Dequeue()

            for origin, target in edges do
                if origin = source && reached.Add(target) then
                    pending.Enqueue(target)

        let errors =
            required
            |> List.filter (fun item -> not (reached.Contains item.RelativePath))
            |> List.map (fun item ->
                Diagnostic.at
                    item.RelativePath
                    1
                    DiagnosticCode.InvalidLink
                    "Document is unreachable from docs/README.md; link it from the map or a reachable document.")

        if
            required.IsEmpty
            || (required |> List.exists (fun item -> item.RelativePath = entry))
        then
            if errors.IsEmpty then Ok() else Error errors
        else
            Error
                [
                    Diagnostic.create
                        DiagnosticCode.InvalidLink
                        "The documentation map docs/README.md is missing."
                ]
