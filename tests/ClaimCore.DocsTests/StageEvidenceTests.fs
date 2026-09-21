module ClaimCore.DocsTests.StageEvidenceTests

open System
open System.IO
open System.Xml.Linq
open ClaimCore.Docs
open Expecto
open ClaimCore.DocsTests.Fixtures

let private stageId = "unit-" + Stages.currentPlatform ()

let private expected =
    Stages.testReports |> List.find (fun report -> report.StageId = stageId)

let private names = expected.ExpectedNames |> Set.toList
let private output = "artifacts/producer"
let private reportPath = output + "/nested/" + expected.FileName

let private ns =
    XNamespace.Get("http://microsoft.com/schemas/VisualStudio/TeamTest/2010")

let private element name (attributes: (string * string) list) (children: XElement list) =
    let value = XElement(ns + name)

    attributes
    |> List.iter (fun (key, text) -> value.SetAttributeValue(XName.Get(key), text))

    children |> List.iter value.Add
    value

let private counters count =
    [ "total", string count; "executed", string count; "passed", string count ]
    @ ([
        "failed"
        "error"
        "timeout"
        "aborted"
        "inconclusive"
        "passedButRunAborted"
        "notRunnable"
        "notExecuted"
        "disconnected"
        "warning"
        "completed"
        "inProgress"
        "pending"
       ]
       |> List.map (fun name -> name, "0"))

let private report testNames assembly outcome =
    let identities =
        testNames |> List.map (fun name -> name, Guid.NewGuid(), Guid.NewGuid())

    let results =
        identities
        |> List.map (fun (name, id, execution) ->
            element
                "UnitTestResult"
                [
                    "testName", name
                    "testId", string id
                    "executionId", string execution
                    "outcome", outcome
                ]
                [])

    let definitions =
        identities
        |> List.map (fun (name, id, execution) ->
            element
                "UnitTest"
                [ "name", name; "id", string id ]
                [
                    element "Execution" [ "id", string execution ] []
                    element "TestMethod" [ "codeBase", assembly + ".dll" ] []
                ])

    let entries =
        identities
        |> List.map (fun (_, id, execution) ->
            element "TestEntry" [ "testId", string id; "executionId", string execution ] [])


    element
        "TestRun"
        [ "id", string (Guid.NewGuid()) ]
        [
            element "Results" [] results
            element "TestDefinitions" [] definitions
            element "TestEntries" [] entries
            element
                "ResultSummary"
                [ "outcome", "Completed" ]
                [ element "Counters" (counters testNames.Length) [] ]
        ]
    |> string

let private record (repository: TempRepository) producer outcome =
    repository.Write("db/postgresql-baseline.json", "{\"containerImage\":\"synthetic-image\"}")
    |> ignore

    let runner =
        QueueRunner
            [
                processOutput 128 "" "Not a Git worktree."
                processOutput 0 "10.0.401" ""
                processOutput 0 "v26.9.0" ""
                processOutput 0 "11.19.1" ""
            ]

    StageCommands.stageManifest
        repository.Root
        runner
        producer
        "producer-test"
        1
        outcome
        "2026-09-21T00:00:00.0000000+00:00"
        "2026-09-21T00:00:01.0000000+00:00"
        output

let private manifestPath producer =
    EvidenceReconciliation.stageManifestPath "producer-test" 1 producer

let private rejects (repository: TempRepository) reason =
    let error = record repository stageId "success" |> requireError
    Expect.stringContains error reason "The producer explains the refusal."

    Expect.isFalse
        (File.Exists(Path.Combine(repository.Path, manifestPath stageId)))
        "An invalid report must not acquire a successful manifest."

let private matchingReport =
    testCase "matching reviewed inventory records a successful producer"
    <| fun _ ->
        use repository = new TempRepository()
        repository.Write(reportPath, report names expected.Assembly "Passed") |> ignore
        repository.Write(output + "/coverage.xml", "<coverage />") |> ignore
        record repository stageId "success" |> requireOk

        let manifest =
            repository.Read(manifestPath stageId)
            |> System.Text.Encoding.UTF8.GetBytes
            |> StageManifestFormat.parse
            |> requireOk

        Expect.equal manifest.Outcome "success" "Exact identities qualify."
        Expect.equal manifest.Output.Files.Length 2 "Coverage remains part of the same output tree."

let private countDrift =
    testCase "missing and additional identities cannot pass the producer"
    <| fun _ ->
        for changed in [ List.tail names; "Unexpected passing test" :: names ] do
            use repository = new TempRepository()

            repository.Write(reportPath, report changed expected.Assembly "Passed")
            |> ignore

            rejects repository "TRX count mismatch"

let private nameDrift =
    testCase "same-count name substitution cannot pass the producer"
    <| fun _ ->
        use repository = new TempRepository()

        repository.Write(
            reportPath,
            report ("Substituted test" :: List.tail names) expected.Assembly "Passed"
        )
        |> ignore

        rejects repository "test names differ"

let private invalidReport =
    testCase "malformed failed and wrong-assembly evidence cannot pass the producer"
    <| fun _ ->
        for content, reason in
            [
                "<TestRun>", "Stage '"
                report names expected.Assembly "Failed", "is not Passed"
                report names "Unregistered.Assembly" "Passed", "wrong test assembly"
            ] do
            use repository = new TempRepository()
            repository.Write(reportPath, content) |> ignore
            rejects repository reason

let private reportSelection =
    testCase "missing duplicate and substituted reports cannot pass the producer"
    <| fun _ ->
        for paths in
            [
                []
                [ reportPath; output + "/other/" + expected.FileName ]
                [ output + "/Wrong.trx" ]
                [ reportPath; output + "/Unrelated.trx" ]
            ] do
            use repository = new TempRepository()
            repository.Write(output + "/coverage.xml", "<coverage />") |> ignore

            for path in paths do
                repository.Write(path, report names expected.Assembly "Passed") |> ignore

            rejects repository "exactly one TRX report"

let private unsuccessfulProducer =
    testCase "unsuccessful producers retain evidence without being relabelled"
    <| fun _ ->
        for outcome in [ "failure"; "cancelled"; "skipped" ] do
            use repository = new TempRepository()
            repository.Write(reportPath, "<incomplete-report />") |> ignore
            record repository stageId outcome |> requireOk

            let manifest =
                repository.Read(manifestPath stageId)
                |> System.Text.Encoding.UTF8.GetBytes
                |> StageManifestFormat.parse
                |> requireOk

            Expect.equal manifest.Outcome outcome "Original unsuccessful outcome is preserved."

let private publication =
    testCase "non-test publication stages do not require a test report"
    <| fun _ ->
        use repository = new TempRepository()

        for path in
            [
                "ClaimCore.Cli.dll"
                "LICENSE"
                "ClaimCore.Cli.cdx.json"
                "THIRD-PARTY-NOTICES.txt"
            ] do
            repository.Write(output + "/" + path, "synthetic") |> ignore

        if OperatingSystem.IsLinux() then
            repository.Write(output + "/libclaimcore_hostsecurity_native.so", "synthetic")
            |> ignore
        elif OperatingSystem.IsMacOS() then
            repository.Write(output + "/libclaimcore_hostsecurity_native.dylib", "synthetic")
            |> ignore

        record repository "publish-cli" "success" |> requireOk

        Expect.isTrue
            (File.Exists(Path.Combine(repository.Path, manifestPath "publish-cli")))
            "A publication still follows its own registered output policy."

let tests =
    testList
        "producer test evidence"
        [
            matchingReport
            countDrift
            nameDrift
            invalidReport
            reportSelection
            unsuccessfulProducer
            publication
        ]
