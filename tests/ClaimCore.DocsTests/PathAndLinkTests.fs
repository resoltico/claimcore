module ClaimCore.DocsTests.PathAndLinkTests

open System
open System.IO
open Expecto
open ClaimCore.Docs
open ClaimCore.DocsTests.Fixtures

let private loaded (repository: TempRepository) relative content =
    let path = repository.Write(relative, content)
    MarkdownModel.read repository.Root path |> requireOk

let private rejectsUnsafeRegisteredPaths () =
    use repository = new TempRepository()

    [ "/outside"; "../outside"; "docs/../README.md"; "docs\\README.md" ]
    |> List.iter (fun path ->
        Repository.registeredPath repository.Root path |> requireError |> ignore)

let private rejectsExistingSymlink () =
    use repository = new TempRepository()

    let outside =
        Path.Combine(Path.GetTempPath(), "claimcore-doc-outside-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(outside) |> ignore
    File.WriteAllText(Path.Combine(outside, "secret.md"), "outside\n")
    let link = Path.Combine(repository.Path, "linked")

    try
        Directory.CreateSymbolicLink(link, outside) |> ignore

        Repository.ensureExistingSafe repository.Root (Path.Combine(link, "secret.md"))
        |> requireError
        |> ignore
    finally
        if Directory.Exists(link) then
            Directory.Delete(link)

        if Directory.Exists(outside) then
            Directory.Delete(outside, true)

let private rejectsWrongCase () =
    use repository = new TempRepository()
    let actual = repository.Write("docs/Exact.md", "# Exact\n")

    let directory =
        Path.GetDirectoryName(actual)
        |> Option.ofObj
        |> Option.defaultValue repository.Path

    let wrong = Path.Combine(directory, "exact.md")
    Repository.ensureExistingSafe repository.Root wrong |> requireError |> ignore

let private listsMarkdownThroughSourceAdmission () =
    use repository = new TempRepository()
    repository.Write("docs/b.md", "# B\n") |> ignore
    repository.Write("docs/a.md", "# A\n") |> ignore
    repository.Write("README.md", "# R\n") |> ignore

    let listed =
        "[\"docs/b.md\",\"docs/a.md\",\"README.md\",\"deleted.md\",\"src/Source.fs\"]"

    let runner = QueueRunner([ processOutput 0 listed "" ])

    let files =
        Repository.markdownFiles repository.Root runner
        |> requireOk
        |> List.map (Repository.relativePath repository.Root)

    Expect.equal
        files
        [ "README.md"; "docs/a.md"; "docs/b.md" ]
        "Ordinal order; deleted files are skipped"

    let request = runner.Requests |> List.exactlyOne
    Expect.equal request.FileName "node" "The shared source admission lists files"

    Expect.equal
        request.Arguments
        [ "eng/ci/repository.mjs"; repository.Root.Path ]
        "The exact source root is explicit; the admission owner scrubs Git redirection"

let private refusesUnlistableRepository () =
    use repository = new TempRepository()
    let runner = QueueRunner([ processOutput 128 "" "fatal: not a git repository" ])
    Repository.markdownFiles repository.Root runner |> requireError |> ignore

let private pathTests =
    testList
        "repository path confinement"
        [
            testCase
                "rejects rooted dot parent and backslash registered paths"
                rejectsUnsafeRegisteredPaths
            testCase "rejects a symlink in an existing path" rejectsExistingSymlink
            testCase "detects exact path casing on every host" rejectsWrongCase
            testCase
                "lists admitted Markdown files in ordinal order"
                listsMarkdownThroughSourceAdmission
            testCase "refuses an unavailable source inventory" refusesUnlistableRepository
        ]

let private navigationTests =
    testCase "documentation navigation rejects orphans and disconnected cycles"
    <| fun _ ->
        use repository = new TempRepository()
        let map = loaded repository "docs/README.md" "# Map\n\n[owner](owner.md#owner)\n"
        let owner = loaded repository "docs/owner.md" "# Owner\n"
        Links.checkNavigation repository.Root [ map; owner ] |> requireOk |> ignore
        let orphan = loaded repository "docs/orphan.md" "# Orphan\n\n[self](#orphan)\n"

        Links.checkNavigation repository.Root [ map; owner; orphan ]
        |> requireError
        |> ignore

        let first = loaded repository "docs/first.md" "# First\n\n[second](second.md)\n"
        let second = loaded repository "docs/second.md" "# Second\n\n[first](first.md)\n"

        Links.checkNavigation repository.Root [ map; owner; first; second ]
        |> requireError
        |> ignore

        let linked = loaded repository "docs/owner.md" "# Owner\n\n[first](first.md)\n"

        Links.checkNavigation repository.Root [ map; linked; first; second ]
        |> requireOk
        |> ignore

let private linkTests =
    testList
        "local links and anchors"
        [
            testCase "accepts checked local external and explicit-anchor links"
            <| fun _ ->
                use repository = new TempRepository()

                let target =
                    loaded
                        repository
                        "docs/target.md"
                        "# Target\n\n<a id=\"stable-anchor\"></a>\n### Stable target\n"

                let source =
                    loaded
                        repository
                        "docs/source.md"
                        "# Source\n\n[heading](target.md#target) [stable](target.md#stable-anchor) [web](https://example.test/)\n"

                Expect.equal
                    (Links.check repository.Root [ source; target ] |> requireOk)
                    3
                    "All three links are classified"

            testCase "rejects missing targets fragments and traversal escapes"
            <| fun _ ->
                use repository = new TempRepository()

                let source =
                    loaded
                        repository
                        "docs/source.md"
                        "# Source\n\n[missing](missing.md) [fragment](#absent) [escape](../../outside.md)\n"

                Links.check repository.Root [ source ] |> requireError |> ignore

            testCase "rejects wrong case malformed encoding and unsafe schemes"
            <| fun _ ->
                use repository = new TempRepository()
                let target = loaded repository "docs/Exact.md" "# Exact\n"

                let source =
                    loaded
                        repository
                        "docs/source.md"
                        "# Source\n\n[case](exact.md) [encoding](bad%2) [file](file:///tmp/no)\n"

                Links.check repository.Root [ source; target ] |> requireError |> ignore

            testCase "rejects duplicate explicit anchors"
            <| fun _ ->
                use repository = new TempRepository()

                let source =
                    loaded
                        repository
                        "docs/source.md"
                        "# Source\n\n<a id=\"same\"></a>\n## One\n\n<a id=\"same\"></a>\n## Two\n"

                Links.check repository.Root [ source ] |> requireError |> ignore
        ]

let tests =
    testList "Path and link safety" [ pathTests; linkTests; navigationTests ]
