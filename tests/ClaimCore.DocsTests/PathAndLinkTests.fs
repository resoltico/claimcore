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

let private listsMarkdownThroughGit () =
    use repository = new TempRepository()
    repository.Write("docs/b.md", "# B\n") |> ignore
    repository.Write("docs/a.md", "# A\n") |> ignore
    repository.Write("README.md", "# R\n") |> ignore
    let listed = "docs/b.md\000docs/a.md\000README.md\000deleted.md\000"
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
    Expect.equal request.FileName "git" "Git lists the files"
    Expect.contains request.Arguments "--exclude-standard" "Git applies the ignore rules"

    Expect.equal
        (request.Environment |> Map.ofList |> Map.tryFind "GIT_DIR")
        (Some None)
        "Git redirection variables are removed"

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
            testCase "lists the Markdown files Git lists, in ordinal order" listsMarkdownThroughGit
            testCase "refuses a repository Git cannot list" refusesUnlistableRepository
        ]

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

let tests = testList "Path and link safety" [ pathTests; linkTests ]
