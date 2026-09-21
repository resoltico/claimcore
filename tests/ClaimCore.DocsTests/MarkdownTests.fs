module ClaimCore.DocsTests.MarkdownTests

open System
open System.IO
open Expecto
open ClaimCore.Docs
open ClaimCore.DocsTests.Fixtures

let private markers =
    "<!-- generated:begin cli-help -->\nold\n<!-- generated:end cli-help -->\n"

let private marker id =
    $"<!-- generated:begin {id} -->\nold\n<!-- generated:end {id} -->\n"

let private syntheticManifest =
    """{"version":1,"components":[{"name":"ClaimCore.Alpha","tier":"product","layer":"core","""
    + """"project":"src/ClaimCore.Alpha/ClaimCore.Alpha.fsproj","role":"Synthetic.","""
    + """"dependsOn":[],"packages":[],"internalsVisibleTo":[],"testInventory":false}]}
"""

/// Every registration must occur exactly once across the supplied documents, so the fixture carries
/// the architecture document and its manifest alongside the three executable help blocks.
let private generatedFixture (repository: TempRepository) (helps: ProcessOutput list) =
    repository.Write(ArchitectureManifest.fileName, syntheticManifest) |> ignore

    let architecture =
        repository.Write(
            "docs/architecture.md",
            "# Architecture\n\n" + marker "architecture-components"
        )
        |> MarkdownModel.read repository.Root
        |> requireOk

    [
        "ClaimCore.Cli", "docs/cli.md", "cli-help", List.item 0 helps
        "ClaimCore.Database", "docs/database.md", "database-help", List.item 1 helps
        "ClaimCore.Web", "docs/web.md", "web-help", List.item 2 helps
    ]
    |> List.map (fun (product, document, id, help) ->
        let binary =
            repository.Write($"artifacts/bin/{product}/release/{product}.dll", "binary")

        let path = repository.Write(document, "# Help\n\n" + marker id)
        let parsed = MarkdownModel.read repository.Root path |> requireOk
        parsed, [ processOutput 0 "build" ""; processOutput 0 (binary + "\n") ""; help ])
    |> List.unzip
    |> fun (documents, outputs) -> architecture :: documents, List.concat outputs

let private blockTests =
    testList
        "generated Markdown blocks"
        [
            testCase "recognizes one exact block and ignores fenced marker examples"
            <| fun _ ->
                let document =
                    markdown
                        "docs/cli.md"
                        ("# CLI\n\n```markdown\n<!-- generated:begin fake -->\n```\n\n" + markers)

                let blocks = MarkdownModel.generatedBlocks document |> requireOk

                Expect.equal
                    (blocks |> List.map _.Id)
                    [ "cli-help" ]
                    "Only structural HTML markers count"

            testCase "rejects nested marker pairs"
            <| fun _ ->
                let document =
                    markdown
                        "docs/cli.md"
                        "<!-- generated:begin cli-help -->\n<!-- generated:begin nested -->\nx\n<!-- generated:end nested -->\n<!-- generated:end cli-help -->\n"

                MarkdownModel.generatedBlocks document |> requireError |> ignore

            testCase "rejects orphaned and malformed marker syntax"
            <| fun _ ->
                let orphan = markdown "docs/cli.md" "<!-- generated:end cli-help -->\n"
                let malformed = markdown "docs/cli.md" "<!-- generated:begin CLI_help -->\nx\n"
                MarkdownModel.generatedBlocks orphan |> requireError |> ignore
                MarkdownModel.generatedBlocks malformed |> requireError |> ignore

            testCase "replaces only the registered body span"
            <| fun _ ->
                let document = markdown "docs/cli.md" ("before\n" + markers + "after\n")
                let block = MarkdownModel.generatedBlocks document |> requireOk |> List.exactlyOne
                let replaced = MarkdownModel.replaceBodies document [ block, "new\n" ]

                Expect.equal
                    replaced
                    "before\n<!-- generated:begin cli-help -->\nnew\n<!-- generated:end cli-help -->\nafter\n"
                    "Authored bytes and marker lines remain intact"
        ]

let private generationSuccessTests =
    testList
        "trusted block output"
        [
            testCase "normalizes only CLI CRLF and emits one final LF"
            <| fun _ ->
                use repository = new TempRepository()

                let documents, outputs =
                    generatedFixture
                        repository
                        [
                            processOutput 0 "line one\r\nline two\r\n" ""
                            processOutput 0 "database" ""
                            processOutput 0 "web" ""
                        ]

                let runner = QueueRunner(outputs)

                let generated =
                    Generators.generate
                        {
                            Root = repository.Root
                            Processes = runner
                        }
                        documents
                    |> requireOk
                    |> List.find (fun item -> item.Original.RelativePath = "docs/cli.md")

                Expect.stringContains
                    generated.ExpectedText
                    "```text\nline one\nline two\n```"
                    "Named normalization"

                Expect.equal runner.Requests.Length 9 "Only fixed registered invocations run"

        ]

let private generationFailureTests =
    testList
        "trusted block failures"
        [
            testCase "fails closed when the trusted producer writes stderr"
            <| fun _ ->
                use repository = new TempRepository()

                let documents, outputs =
                    generatedFixture
                        repository
                        [
                            processOutput 0 "help" "unexpected"
                            processOutput 0 "database" ""
                            processOutput 0 "web" ""
                        ]

                let runner = QueueRunner(outputs)

                Generators.generate
                    {
                        Root = repository.Root
                        Processes = runner
                    }
                    documents
                |> requireError
                |> ignore

            testCase "requires registration in both directions"
            <| fun _ ->
                let unknown =
                    markdown
                        "docs/cli.md"
                        "<!-- generated:begin unknown -->\nx\n<!-- generated:end unknown -->\n"

                let runner = QueueRunner([])

                Generators.generate
                    {
                        Root = RepositoryRoot.CreateForTests("/virtual")
                        Processes = runner
                    }
                    [ unknown ]
                |> requireError
                |> ignore
        ]

let private generationTests =
    testList "trusted block generators" [ generationSuccessTests; generationFailureTests ]

let private contractTests =
    testList
        "contract declarations"
        [
            testCase "extracts an anchored contract heading"
            <| fun _ ->
                let document =
                    markdown
                        "docs/domain.md"
                        "# Domain\n\n<a id=\"cc-dom-001\"></a>\n### CC-DOM-001 — Exact record\n\nMeaning.\n"

                let declaration =
                    Contracts.declarations [ document ] |> requireOk |> List.exactlyOne

                Expect.equal declaration.Id "CC-DOM-001" "Stable ID"

            testCase "rejects a missing anchor and duplicate contract ID"
            <| fun _ ->
                let missing = markdown "docs/a.md" "### CC-DOM-001 — Exact record\n"

                let duplicate =
                    markdown
                        "docs/b.md"
                        "<a id=\"cc-dom-001\"></a>\n### CC-DOM-001 — Other wording\n"

                Contracts.declarations [ missing; duplicate ] |> requireError |> ignore
        ]

let tests =
    testList "Markdown assurance" [ blockTests; generationTests; contractTests ]
