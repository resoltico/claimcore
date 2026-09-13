module ClaimCore.DocsTests.RepositoryPolicyAssertions

open System
open System.IO
open Expecto
open ClaimCore.Docs
open ClaimCore.DocsTests.Fixtures

let private paths (repository: TempRepository) =
    RepositoryInventory.sourceFiles repository.Root

let private relativePaths (repository: TempRepository) =
    paths repository |> List.map (Repository.relativePath repository.Root)

let private excludedFallbackPaths =
    [
        ".idea/workspace.xml"
        ".history/source.fs"
        ".vscode/session.json"
        ".vscode/session.md"
        ".vitest/results.json"
        "BenchmarkDotNet.Artifacts/report.html"
        "TestResults/result.trx"
        "coverage/coverage.json"
        "nested/bin/output.dll"
        "nested/obj/project.assets.json"
        "nested/playwright-report/index.html"
        "nested/test-results/result.json"
        "secrets/.env.production"
        "secrets/.envrc.local"
        "secrets/.npmrc"
        "secrets/.netrc"
        "secrets/_netrc"
        "secrets/app.connection"
        "secrets/bootstrap-credential-current"
        "secrets/bootstrap-credential-retired.txt"
        "secrets/claimcore-recovery-current.json"
        "secrets/signing.key"
        "secrets/web.pfx"
        "tmp/build.binlog"
        "tmp/debug.suo"
        "tmp/debug.rsuser"
        "tmp/npm-debug.log.1"
        "tmp/package.nupkg"
        "tmp/source.fs.orig"
        "tmp/source.fs.swp"
        "tmp/test.coveragexml"
        "tmp/types.tsbuildinfo"
        "web/.eslintcache"
        "web/.local/browser-profile/History"
        "web/.stylelintcache"
        "web/blob-report/report.zip"
        "web/playwright/.auth/session.json"
        "web/playwright/.cache/browser.json"
    ]

let private writeFallbackInputs (repository: TempRepository) =
    excludedFallbackPaths
    |> List.iter (fun relative -> repository.Write(relative, "private\n") |> ignore)

    repository.Write(".env.example", "PUBLIC_SETTING=example\n") |> ignore
    repository.Write("config/.env.example", "PUBLIC_SETTING=nested\n") |> ignore
    repository.Write(".vscode/settings.json", "{}\n") |> ignore
    repository.Write("web/.npmrc", "audit=true\n") |> ignore
    repository.Write("src/claimcore-recovery-probe.fs", "let value = 1\n")

let private assertFallbackClassification (repository: TempRepository) =
    let relative = relativePaths repository

    let markdown =
        Repository.markdownFiles repository.Root
        |> List.map (Repository.relativePath repository.Root)

    [ ".env.example"; ".vscode/settings.json"; "config/.env.example"; "web/.npmrc" ]
    |> List.iter (fun path -> Expect.contains relative path "Public repository input")

    Expect.contains
        relative
        "src/claimcore-recovery-probe.fs"
        "Executable source-like names cannot evade identity"

    excludedFallbackPaths
    |> List.iter (fun path ->
        Expect.isFalse
            (relative |> List.contains path)
            $"Local/private path must be excluded: {path}")

    Expect.isFalse
        (markdown |> List.contains ".vscode/session.md")
        "Markdown discovery shares the local-file policy"

let private assertFallbackHashing (repository: TempRepository) source =
    let beforePrivateChange =
        paths repository |> RepositoryInventory.aggregateHash repository.Root

    repository.Write("secrets/app.connection", "changed-private\n") |> ignore

    let afterPrivateChange =
        paths repository |> RepositoryInventory.aggregateHash repository.Root

    Expect.equal
        afterPrivateChange
        beforePrivateChange
        "Ignored private bytes do not affect fallback provenance"

    File.WriteAllText(source, "let value = 2\n")

    let afterSourceChange =
        paths repository |> RepositoryInventory.aggregateHash repository.Root

    Expect.notEqual afterSourceChange beforePrivateChange "Authored source changes identity"

let private assertFallbackSymlinkRejection (repository: TempRepository) source =
    let linked = Path.Combine(repository.Path, "src/linked.fs")
    File.CreateSymbolicLink(linked, source) |> ignore
    Expect.throws (fun () -> paths repository |> ignore) "Inventory rejects symbolic links"

let validateFallbackInventory () =
    use repository = new TempRepository()
    let source = writeFallbackInputs repository
    assertFallbackClassification repository
    assertFallbackHashing repository source
    assertFallbackSymlinkRejection repository source

let private nulList values =
    String.concat (string (char 0)) values + string (char 0)

let private gitOutputs status files revision =
    [
        processOutput 0 "true\n" ""
        processOutput 0 status ""
        processOutput 0 (nulList files) ""
        processOutput 0 (revision + "\n") ""
    ]

let private redirectVariables =
    [
        "GIT_ALTERNATE_OBJECT_DIRECTORIES"
        "GIT_CEILING_DIRECTORIES"
        "GIT_COMMON_DIR"
        "GIT_CONFIG_COUNT"
        "GIT_CONFIG_PARAMETERS"
        "GIT_DIR"
        "GIT_DISCOVERY_ACROSS_FILESYSTEM"
        "GIT_INDEX_FILE"
        "GIT_NAMESPACE"
        "GIT_OBJECT_DIRECTORY"
        "GIT_WORK_TREE"
    ]

let private expectGitEnvironmentScrubbed (runner: QueueRunner) =
    runner.Requests
    |> List.filter (fun request -> request.FileName = "git")
    |> List.iter (fun request ->
        let environment = request.Environment |> Map.ofList

        redirectVariables
        |> List.iter (fun name ->
            Expect.equal
                (environment |> Map.tryFind name)
                (Some None)
                $"Git redirect variable is removed: {name}"))

let private writeGitInputs (repository: TempRepository) =
    repository.Write("src/code.fs", "let value = 1\n") |> ignore
    repository.Write("secrets/tracked.pem", "tracked-private-looking\n") |> ignore
    repository.Write("secrets/ignored.connection", "ignored-private\n") |> ignore

    [
        "ClaimCore.slnx"
        "Directory.Build.props"
        "secrets/tracked.pem"
        "src/code.fs"
    ]

let private validIdentity markerIsFile =
    use repository = new TempRepository()

    if markerIsFile then
        repository.Write(".git", "gitdir: /synthetic/worktree\n") |> ignore
    else
        Directory.CreateDirectory(Path.Combine(repository.Path, ".git")) |> ignore

    let files = writeGitInputs repository
    let revision = String.replicate 40 "a"
    let runner = QueueRunner(gitOutputs "" files revision)
    let first = Provenance.sourceIdentity repository.Root runner |> requireOk
    Expect.equal first.GitRevision (Some revision) "Revision"
    Expect.equal first.State "clean" "Clean state"
    expectGitEnvironmentScrubbed runner

    repository.Write("secrets/ignored.connection", "changed-ignored\n") |> ignore
    let ignoredRunner = QueueRunner(gitOutputs "" files revision)

    let afterIgnored =
        Provenance.sourceIdentity repository.Root ignoredRunner |> requireOk

    Expect.equal
        afterIgnored.ContentSha256
        first.ContentSha256
        "Git's nonignored inventory excludes ignored private state"

    repository.Write("secrets/tracked.pem", "changed-tracked\n") |> ignore

    let trackedRunner =
        QueueRunner(gitOutputs " M secrets/tracked.pem\n" files revision)

    let afterTracked =
        Provenance.sourceIdentity repository.Root trackedRunner |> requireOk

    Expect.equal afterTracked.State "dirty" "Tracked change is dirty"

    Expect.notEqual
        afterTracked.ContentSha256
        first.ContentSha256
        "Force-tracked private-looking files remain provenance inputs"

let validateGitProvenance () =
    validIdentity false
    validIdentity true

    use nonRepository = new TempRepository()
    let nonRepositoryRunner = QueueRunner([ processOutput 128 "" "not a repository" ])

    let unversioned =
        Provenance.sourceIdentity nonRepository.Root nonRepositoryRunner |> requireOk

    Expect.equal unversioned.State "unversioned" "A real non-repository uses fallback inventory"
    expectGitEnvironmentScrubbed nonRepositoryRunner

    use unbornRepository = new TempRepository()
    Directory.CreateDirectory(Path.Combine(unbornRepository.Path, ".git")) |> ignore
    let unbornFiles = [ "ClaimCore.slnx"; "Directory.Build.props" ]

    let unbornRunner =
        QueueRunner(
            [
                processOutput 0 "true\n" ""
                processOutput 0 "?? ClaimCore.slnx\n" ""
                processOutput 0 (nulList unbornFiles) ""
                processOutput 128 "" "unknown revision"
                processOutput 0 "refs/heads/main\n" ""
            ]
        )

    let unborn =
        Provenance.sourceIdentity unbornRepository.Root unbornRunner |> requireOk

    Expect.equal unborn.GitRevision None "An unborn repository has no commit"
    Expect.equal unborn.State "unborn" "Unborn is distinct from unversioned"

    use corruptRepository = new TempRepository()
    corruptRepository.Write(".git", "broken\n") |> ignore
    let corruptRunner = QueueRunner([ processOutput 128 "" "broken repository" ])

    Provenance.sourceIdentity corruptRepository.Root corruptRunner
    |> requireError
    |> ignore

    use malformedInventory = new TempRepository()

    Directory.CreateDirectory(Path.Combine(malformedInventory.Path, ".git"))
    |> ignore

    let malformedRunner =
        QueueRunner(
            [
                processOutput 0 "true\n" ""
                processOutput 0 "" ""
                processOutput 0 "ClaimCore.slnx" ""
            ]
        )

    Provenance.sourceIdentity malformedInventory.Root malformedRunner
    |> requireError
    |> ignore
