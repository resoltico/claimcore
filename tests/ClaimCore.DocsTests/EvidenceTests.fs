module ClaimCore.DocsTests.EvidenceTests

open System
open System.IO
open System.Text
open Expecto
open ClaimCore.Docs
open ClaimCore.DocsTests.Fixtures

let private testId = "11111111-1111-4111-8111-111111111111"
let private executionId = "44444444-4444-4444-8444-444444444444"
let private runId = "22222222-2222-4222-8222-222222222222"

let private trx name outcome counters codeBase resultId =
    $"""<?xml version="1.0" encoding="utf-8"?>
<TestRun id="{runId}" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results><UnitTestResult executionId="{executionId}" testId="{resultId}" testName="{name}" outcome="{outcome}" /></Results>
  <TestDefinitions><UnitTest name="{name}" id="{testId}"><Execution id="{executionId}" /><TestMethod codeBase="{codeBase}" /></UnitTest></TestDefinitions>
  <TestEntries><TestEntry testId="{testId}" executionId="{executionId}" /></TestEntries>
  <ResultSummary outcome="Completed"><Counters {counters} /></ResultSummary>
</TestRun>
"""

let private passingCounters =
    "total=\"1\" executed=\"1\" passed=\"1\" failed=\"0\" error=\"0\" timeout=\"0\" aborted=\"0\" inconclusive=\"0\" passedButRunAborted=\"0\" notRunnable=\"0\" notExecuted=\"0\" disconnected=\"0\" warning=\"0\" completed=\"0\" inProgress=\"0\" pending=\"0\""

let private expected =
    {
        StageId = "unit-linux"
        Assembly = "ClaimCore.Tests"
        FileName = "ClaimCore.Tests.trx"
        ExpectedTests = 1
        ExpectedNames = set [ "[CC-DOM-001] exact fields" ]
    }

let private parse (content: string) =
    let path =
        Path.Combine(Path.GetTempPath(), "claimcore-trx-" + Guid.NewGuid().ToString("N") + ".trx")

    File.WriteAllText(path, content, UTF8Encoding(false))

    try
        Evidence.parseTrx expected path
    finally
        File.Delete(path)

let private acceptsPassingExecution () =
    let report =
        trx "[CC-DOM-001] exact fields" "Passed" passingCounters "/tmp/ClaimCore.Tests.dll" testId
        |> parse
        |> requireOk

    Expect.equal report.Total 1 "One result"
    Expect.equal report.Tests.Head.Name "[CC-DOM-001] exact fields" "Display name retained"

    trx
        "[CC-DOM-001] exact fields"
        "Passed"
        passingCounters
        "D:\\a\\claimcore\\ClaimCore.Tests.dll"
        testId
    |> parse
    |> requireOk
    |> ignore

let private rejectsInvalidCounters () =
    let rejected counters =
        trx "plain" "Passed" counters "/tmp/ClaimCore.Tests.dll" testId
        |> parse
        |> requireError
        |> ignore

    passingCounters.Replace("notExecuted=\"0\"", "notExecuted=\"1\"") |> rejected

    passingCounters.Replace("passedButRunAborted=\"0\"", "passedButRunAborted=\"1\"")
    |> rejected

    passingCounters + " invented=\"0\"" |> rejected

let private rejectsOutcomeAndAssembly () =
    trx "plain" "Failed" passingCounters "/tmp/ClaimCore.Tests.dll" testId
    |> parse
    |> requireError
    |> ignore

    trx "plain" "Passed" passingCounters "/tmp/Other.Tests.dll" testId
    |> parse
    |> requireError
    |> ignore

let private rejectsUnresolvedResult () =
    trx
        "plain"
        "Passed"
        passingCounters
        "/tmp/ClaimCore.Tests.dll"
        "33333333-3333-4333-8333-333333333333"
    |> parse
    |> requireError
    |> ignore

let private rejectsNamespaceCountAndName () =
    let valid = trx "plain" "Passed" passingCounters "/tmp/ClaimCore.Tests.dll" testId

    trx "renamed leaf" "Passed" passingCounters "/tmp/ClaimCore.Tests.dll" testId
    |> parse
    |> requireError
    |> ignore

    valid.Replace("http://microsoft.com/schemas/VisualStudio/TeamTest/2010", "urn:wrong")
    |> parse
    |> requireError
    |> ignore

    let wrongCount = { expected with ExpectedTests = 2 }
    let path = Path.GetTempFileName()

    try
        File.WriteAllText(path, valid)
        Evidence.parseTrx wrongCount path |> requireError |> ignore
    finally
        File.Delete(path)

let private rejectsExecutionEntries () =
    let valid = trx "plain" "Passed" passingCounters "/tmp/ClaimCore.Tests.dll" testId

    valid.Replace(
        $"<UnitTestResult executionId=\"{executionId}\"",
        "<UnitTestResult executionId=\"55555555-5555-4555-8555-555555555555\""
    )
    |> parse
    |> requireError
    |> ignore

    valid.Replace(
        $"  <TestEntries><TestEntry testId=\"{testId}\" executionId=\"{executionId}\" /></TestEntries>\n",
        ""
    )
    |> parse
    |> requireError
    |> ignore

let private trxAcceptanceTests =
    testList
        "accepted TRX evidence"
        [
            testCase "accepts one fully reconciled passing execution" acceptsPassingExecution
            testCase "rejects nonzero skipped counters even with a pass" rejectsInvalidCounters
        ]

let private trxRejectionTests =
    testList
        "rejected TRX evidence"
        [
            testCase "rejects failed outcomes and wrong assembly identity" rejectsOutcomeAndAssembly
            testCase "rejects an unresolved result ID" rejectsUnresolvedResult
            testCase "rejects wrong namespace and exact-count drift" rejectsNamespaceCountAndName
            testCase
                "rejects missing or mismatched execution-entry identities"
                rejectsExecutionEntries
        ]

let private trxTests =
    testList "strict TRX reconciliation" [ trxAcceptanceTests; trxRejectionTests ]

let private declaration id =
    {
        Id = id
        Document = "docs/domain.md"
        Line = 1
        SectionBytes = Encoding.UTF8.GetBytes("contract section")
    }

let private report names =
    {
        Assembly = "ClaimCore.Tests"
        RunId = Guid.Parse(runId)
        Total = names |> List.length
        Passed = names |> List.length
        Tests =
            names
            |> List.mapi (fun index name ->
                {
                    Name = name
                    TestId = Guid.Parse($"00000000-0000-4000-8000-{index + 1:D12}")
                    Outcome = "Passed"
                    Assembly = "ClaimCore.Tests"
                })
    }

let private contractTests =
    testList
        "contract execution mapping"
        [
            testCase "maps exact leading tags to every declaration"
            <| fun _ ->
                let declarations = [ declaration "CC-DOM-001"; declaration "CC-APP-001" ]

                let executions =
                    report [ "suite.[CC-DOM-001] fields"; "suite.group.[CC-APP-001] rejection" ]

                let evidence =
                    EvidenceReconciliation.contracts declarations [ executions ] |> requireOk

                Expect.equal evidence.Length 2 "Both contracts have executed evidence"

            testCase "rejects missing unknown and malformed contract evidence"
            <| fun _ ->
                let declarations = [ declaration "CC-DOM-001" ]

                [
                    report [ "ordinary test" ]
                    report [ "[CC-APP-999] unknown" ]
                    report [ "[CC-DOM-001]missing-space" ]
                ]
                |> List.iter (fun execution ->
                    EvidenceReconciliation.contracts declarations [ execution ]
                    |> requireError
                    |> ignore)
            testCase "rejects an unknown Web leaf token despite valid declared evidence"
            <| fun _ ->
                let declarations = [ declaration "CC-WEB-001" ]

                let errors =
                    report [ "[CC-WEB-001] admitted"; "[CC-WEB-083] undeclared" ]
                    |> List.singleton
                    |> EvidenceReconciliation.contracts declarations
                    |> requireError

                Expect.contains
                    errors
                    "Test names unknown contract 'CC-WEB-083'."
                    "An undeclared Web tag cannot hide behind another passing contract leaf"
        ]

let private failureEvidenceTests =
    testList
        "failure evidence"
        [
            testCase "wrong structured JSON kinds return a bounded validation error"
            <| fun _ ->
                let invalid =
                    """{"format":1,"formatVersion":"one","status":[],"totals":{},"tests":{}}"""
                    |> Encoding.UTF8.GetBytes

                StructuredReports.validateVitestBytes invalid |> requireError |> ignore

            testCase "the evidence command writes an honest report before failing"
            <| fun _ ->
                use repository = new TempRepository()

                Directory.CreateDirectory(
                    Path.Combine(repository.Path, "artifacts/evidence/run-1/1")
                )
                |> ignore

                let runner = QueueRunner([])
                let exit = Commands.run repository.Root runner [ "evidence"; "run-1"; "1" ]
                Expect.equal exit ExitCode.CheckFailed "Incomplete evidence fails"
                let report = repository.Read("artifacts/evidence/run-1/1/evidence.json")
                Expect.stringContains report "\"outcome\": \"failed\"" "Failure is recorded"
                Expect.stringContains report "\"notRun\"" "Absent stages are explicit"
        ]

let tests =
    testList "Execution evidence" [ trxTests; contractTests; failureEvidenceTests ]
