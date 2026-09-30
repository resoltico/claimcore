module ClaimCore.DocsTests.TestReportMergeTests

open System.IO
open Expecto
open ClaimCore.Docs
open ClaimCore.DocsTests.Fixtures

let private counters total =
    $"total=\"{total}\" executed=\"{total}\" passed=\"{total}\" failed=\"0\" error=\"0\" timeout=\"0\" aborted=\"0\" inconclusive=\"0\" passedButRunAborted=\"0\" notRunnable=\"0\" notExecuted=\"0\" disconnected=\"0\" warning=\"0\" completed=\"0\" inProgress=\"0\" pending=\"0\""

let private guid (digit: char) =
    System.String(digit, 8)
    + "-"
    + System.String(digit, 4)
    + "-4"
    + System.String(digit, 3)
    + "-8"
    + System.String(digit, 3)
    + "-"
    + System.String(digit, 12)

let private execution (digit: char) = guid (char (int digit + 48))

/// One partition report holding one passing test, with identities derived from `digit`.
let private partition (digit: char) (name: string) (outcome: string) =
    $"""<?xml version="1.0" encoding="utf-8"?>
<TestRun id="{guid digit}" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times creation="2026-09-30T05:00:0{digit}.0000000+00:00" queuing="2026-09-30T05:00:0{digit}.0000000+00:00" start="2026-09-30T05:00:0{digit}.0000000+00:00" finish="2026-09-30T05:10:0{digit}.0000000+00:00" />
  <Results><UnitTestResult executionId="{execution digit}" testId="{guid digit}" testName="{name}" outcome="{outcome}" /></Results>
  <TestDefinitions><UnitTest name="{name}" id="{guid digit}"><Execution id="{execution digit}" /><TestMethod codeBase="/tmp/ClaimCore.Tests.dll" /></UnitTest></TestDefinitions>
  <TestEntries><TestEntry testId="{guid digit}" executionId="{execution digit}" /></TestEntries>
  <ResultSummary outcome="Completed"><Counters {counters 1} /></ResultSummary>
</TestRun>
"""

let private expected (names: string list) =
    {
        StageId = "unit-linux"
        Assembly = "ClaimCore.Tests"
        FileName = "ClaimCore.Tests.trx"
        ExpectedTests = names.Length
        ExpectedNames = Set.ofList names
    }

let private write (repository: TempRepository) name content =
    let relative = $"artifacts/parts/{name}.trx"
    repository.Write(relative, content) |> ignore
    relative

let private mergesDisjointReports () =
    use repository = new TempRepository()
    let first = write repository "one" (partition '1' "first" "Passed")
    let second = write repository "two" (partition '2' "second" "Passed")
    let output = "artifacts/merged/ClaimCore.Tests.trx"

    Expect.equal
        (TestReportMerge.merge repository.Root "ClaimCore.Tests" output [ first; second ]
         |> requireOk)
        2
        "Both tests are merged"

    Expect.isOk
        (LocalTestReports.verifyDefinition repository.Root (expected [ "first"; "second" ]) output)
        "The merged report verifies against the exact union of names, counters and identities"

    Expect.isError
        (LocalTestReports.verifyDefinition repository.Root (expected [ "first" ]) output)
        "A merged report is not accepted for a smaller registered set"

let private refusesOverlap () =
    use repository = new TempRepository()
    let first = write repository "one" (partition '1' "same" "Passed")
    let second = write repository "two" (partition '2' "same" "Passed")

    Expect.isError
        (TestReportMerge.merge
            repository.Root
            "ClaimCore.Tests"
            "artifacts/merged/ClaimCore.Tests.trx"
            [ first; second ])
        "A test in two partitions is a defect, not a merge"

let private refusesNonPassingInput () =
    use repository = new TempRepository()
    let first = write repository "one" (partition '1' "first" "Passed")
    let second = write repository "two" (partition '2' "second" "Failed")

    Expect.isError
        (TestReportMerge.merge
            repository.Root
            "ClaimCore.Tests"
            "artifacts/merged/ClaimCore.Tests.trx"
            [ first; second ])
        "One failing partition cannot be merged into a passing report"

    Expect.isFalse
        (File.Exists(Path.Combine(repository.Path, "artifacts/merged/ClaimCore.Tests.trx")))
        "No report is written when any input is refused"

let private refusesExistingOutput () =
    use repository = new TempRepository()
    let first = write repository "one" (partition '1' "first" "Passed")
    let output = write repository "existing" (partition '2' "second" "Passed")

    Expect.isError
        (TestReportMerge.merge repository.Root "ClaimCore.Tests" output [ first ])
        "An existing report is never overwritten"

let tests =
    testList
        "test report merge"
        [
            testCase
                "merges disjoint partition reports into one verifiable report"
                mergesDisjointReports
            testCase "refuses partitions that share a test" refusesOverlap
            testCase "refuses a partition with a non-passing test" refusesNonPassingInput
            testCase "refuses to overwrite an existing report" refusesExistingOutput
        ]
