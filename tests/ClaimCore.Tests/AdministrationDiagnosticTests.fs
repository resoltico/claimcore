module ClaimCore.Tests.AdministrationDiagnosticTests

open System
open System.IO
open System.Text.Json
open Microsoft.FSharp.Reflection
open Expecto
open ClaimCore.Database
open ClaimCore.Postgres
open ClaimCore.Tests.DiagnosticTestStreams

let private inputCauses () =
    for arguments, expected in
        [
            [ "migrate" ], DatabaseInputProblem.UnsupportedInvocation
            [ "set-business-zone"; "Etc/UTC" ], DatabaseInputProblem.UnsupportedInvocation
            [ "initialize" ], DatabaseInputProblem.UnsupportedInvocation
            [ "verify"; "--force" ], DatabaseInputProblem.UnsupportedInvocation
            [ "prune"; "--PRIVATE-UNKNOWN" ], DatabaseInputProblem.UnknownOption
            [ "prune"; "--limit" ], DatabaseInputProblem.MissingOptionValue DatabaseOption.Limit
            [ "prune"; "--limit"; "--dry-run" ],
            DatabaseInputProblem.MissingOptionValue DatabaseOption.Limit
            [ "prune"; "--limit"; "PRIVATE-VALUE" ],
            DatabaseInputProblem.OptionOutOfRange DatabaseOption.Limit
            [ "prune"; "--limit"; "1001" ],
            DatabaseInputProblem.OptionOutOfRange DatabaseOption.Limit
            [ "prune"; "--dry-run"; "--dry-run" ],
            DatabaseInputProblem.RepeatedOption DatabaseOption.DryRun
        ] do
        match DatabaseArguments.parse arguments with
        | Error actual ->
            Expect.equal actual expected "Specific argument admission cause"

            let encoded =
                System.Text.Encoding.UTF8.GetString(DatabaseDiagnostics.inputFailure actual)

            Expect.isFalse
                (encoded.Contains("PRIVATE-", StringComparison.Ordinal))
                "Unknown options and values are never echoed"
        | Ok _ -> failtest "Malformed invocation was admitted"

let private closedNativeReasons () =
    let cases = FSharpType.GetUnionCases typeof<AdministrationFailure>

    Expect.equal
        cases.Length
        DatabaseDiagnostics.nativeReasons.Length
        "All native reasons have outward identities"

    Expect.isTrue
        (cases |> Array.forall (fun value -> value.GetFields().Length = 0))
        "No provider or argument payloads"

    let ids =
        DatabaseDiagnostics.nativeReasons |> List.map DatabaseDiagnostics.nativeToken

    Expect.equal ids.Length (Set.ofList ids |> Set.count) "No duplicate native identity"

let private completedDelivery () =
    use output = new BrokenOutput(false)
    use errors = new MemoryStream()

    let result: AdministrationOutcome<PreparationPruneResult option> =
        AdministrationOutcome.Completed None

    Expect.equal
        (DatabaseExecution.deliver DatabaseCommand.Verify result output errors)
        3
        "Completed operation, failed reporting"

    let value = parsed errors

    Expect.equal
        (value.GetProperty("operationOutcome").GetString())
        "COMPLETED"
        "A broken stdout cannot undo confirmed completion"

    Expect.equal
        (diagnosticId value)
        "DB_OUTPUT_DELIVERY_FAILED"
        "Output failure is not maintenance failure"

    Expect.equal output.Writes 1 "No second stdout document"

let private unknownDelivery () =
    use output = new MemoryStream()
    use errors = new BrokenOutput(false)

    let result: AdministrationOutcome<PreparationPruneResult option> =
        AdministrationOutcome.CompletionUnknown AdministrationFailure.CommitUnconfirmed

    Expect.equal
        (DatabaseExecution.deliver DatabaseCommand.Verify result output errors)
        4
        "Lost commit confirmation stays uncertain"

    Expect.equal errors.Writes 1 "Never append a second JSON document to a partly written stderr"
    Expect.equal output.Length 0L "No false success"

let private processKnowledge () =
    use value =
        JsonDocument.Parse(DatabaseDiagnostics.inputFailure DatabaseInputProblem.ProcessFailed)

    Expect.equal
        (value.RootElement.GetProperty("operationOutcome").GetString())
        "COMPLETION_UNKNOWN"
        "An unexpected process failure is not a pre-admission guarantee"

    Expect.equal
        (value.RootElement.GetProperty("recommendedAction").GetString())
        "INSPECT_AND_RECONCILE"
        "No automatic retry recommendation"

let private exactCounters () =
    let counts =
        {
            CandidateCount = 1
            DeletedCount = 0
            DryRun = true
            TerminalPreparationCount = 9007199254740993L
            TerminalCanonicalRequestBytes = Int64.MaxValue
        }

    use value =
        JsonDocument.Parse(
            DatabaseDiagnostics.outcome
                (DatabaseCommand.Prune PreparationPruneOptions.defaults)
                (AdministrationOutcome.Completed(Some counts))
        )

    let maintenance = value.RootElement.GetProperty("maintenance")

    Expect.equal
        (maintenance.GetProperty("terminalPreparationCount").GetString())
        "9007199254740993"
        "Counters above IEEE-754 exact range are decimal strings"

    Expect.equal
        (maintenance.GetProperty("terminalCanonicalRequestBytes").GetString())
        "9223372036854775807"
        "No lossy double conversion"

let tests =
    testList
        "administration diagnostic boundaries"
        [
            testCase "argument causes are precise without echoing unknown input" inputCauses
            testCase
                "native administrative reasons have a complete closed vocabulary"
                closedNativeReasons
            testCase "output failure cannot relabel confirmed maintenance" completedDelivery
            testCase
                "failed stderr cannot change commit uncertainty or append another frame"
                unknownDelivery
            testCase "unexpected process failure makes no not-started claim" processKnowledge
            testCase "administration counters retain exact 64-bit values" exactCounters
        ]
