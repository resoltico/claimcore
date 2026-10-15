module ClaimCore.AcceptanceTests.ProcessFailureTests

open System
open System.Diagnostics
open System.Text
open Expecto
open ClaimCore.TestSupport

let private start script input =
    let child =
        ProcessStartInfo(
            "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input
        )

    child.ArgumentList.Add "-c"
    child.ArgumentList.Add script
    child

let private failure kind script (input: byte array option) maximum timeout =
    try
        BoundedProcess.run (start script input.IsSome) input maximum timeout |> ignore
        failtest "The synthetic child must exceed its configured process contract."
    with :? BoundedProcessFailure as failed ->
        Expect.equal failed.Kind kind "Safe failure category"
        Expect.isLessThanOrEqual failed.StandardOutput.Length maximum "Bounded partial stdout"
        Expect.isLessThanOrEqual failed.StandardError.Length maximum "Bounded partial stderr"

        Expect.isFalse
            (failed.Message.Contains("synthetic"))
            "Exception text contains no child output"

        failed

let private outputControls () =
    let output = failure Output "printf synthetic-overflow" None 4 2000

    Expect.equal
        (Encoding.UTF8.GetString output.StandardOutput)
        "synt"
        "Overflow retains only allowance"

    let errors = failure Output "printf synthetic-overflow >&2" None 4 2000

    Expect.equal
        (Encoding.UTF8.GetString errors.StandardError)
        "synt"
        "Stderr has the same allowance"

let private cleanupControls () =
    let bytes () =
        Encoding.UTF8.GetBytes "synthetic-private-output"

    for cleanup in [ (fun () -> false); (fun () -> invalidOp "synthetic-cleanup-detail") ] do
        let evidence = BoundedProcess.captureFailure Deadline bytes bytes cleanup
        Expect.equal evidence.Kind Deadline "Cleanup cannot overwrite the main failure"
        Expect.isTrue evidence.CleanupFailed "Cleanup race or incomplete cleanup is explicit"
        Expect.equal evidence.StandardOutput (bytes ()) "Cleanup cannot discard private evidence"
        Expect.equal evidence.StandardError (bytes ()) "Stderr remains available"

        Expect.isFalse
            (evidence.Message.Contains("synthetic"))
            "Cleanup details stay out of failure text"

    let completed = BoundedProcess.captureFailure Delivery bytes bytes (fun () -> true)
    Expect.isFalse completed.CleanupFailed "Completed cleanup has no failure flag"

let private progressControls () =
    let row = "  ✓  1 [firefox] › e2e/example.spec.ts:4:1 › private test title (1.2s)"

    let failed =
        "  ✘  2 [firefox] › e2e/example.spec.ts:8:1 › private failure title (33.6s)"

    let count text =
        Encoding.UTF8.GetBytes(text: string) |> BrowserProgress.counts

    Expect.equal (count (row + "\n" + row + "\n" + failed)) (1, 1) "Unique completed reporter rows"
    Expect.equal (count ("\u001b[32m" + row + "\u001b[0m")) (1, 0) "Terminal color is removed"

    for unrelated in
        [
            "229 passed (4.7m)"
            "payload: " + row
            row.Replace("[firefox]", "[unknown]")
            row.Replace("✓  1", "✓  0")
            row.Replace("e2e/example.spec.ts:4:1", "example")
            row.Replace("(1.2s)", "(unknown)")
            row.Replace("[firefox]", "\n[firefox]")
            row.Substring(0, row.Length - 3)
        ] do
        Expect.equal
            (count unrelated)
            (0, 0)
            "Unrecognized or partial lines cannot inflate progress"

let private locationControls () =
    let row =
        "  ✘  29 [chromium] › e2e/localization-editor.spec.ts:58:1 › private payload/title (30.0s)"

    let locations value =
        Encoding.UTF8.GetBytes(value: string) |> BrowserProgress.failedLocations

    Expect.equal
        (locations row)
        [ "localization-editor.spec.ts:58" ]
        "Only a code location is retained"

    for changed in
        [
            "private payload " + row
            row.Replace("e2e/localization-editor.spec.ts:58:1", "/private/credential.json:58:1")
            row.Replace("e2e/localization-editor.spec.ts:58:1", "e2e/../private.spec.ts:58:1")
            row.Replace(":58:1", ":0:1")
            row.Replace(":58:1", ":58:0")
            row.Replace("[chromium]", "[private-title]")
        ] do
        Expect.isEmpty (locations changed) "Private or malformed locations are omitted"

    Expect.equal
        (locations (
            row.Replace("private payload/title", "https://private.invalid/token?secret=value")
        ))
        [ "localization-editor.spec.ts:58" ]
        "Even reporter titles containing private details are never exposed"

let private controls () =
    cleanupControls ()
    progressControls ()
    locationControls ()

    let deadline =
        failure Deadline "printf synthetic-out; printf synthetic-err >&2; sleep 2" None 64 250

    Expect.equal (Encoding.UTF8.GetString deadline.StandardOutput) "synthetic-out" "Deadline stdout"
    Expect.equal (Encoding.UTF8.GetString deadline.StandardError) "synthetic-err" "Deadline stderr"

    outputControls ()

    let delivery =
        failure
            Delivery
            "printf synthetic-out; printf synthetic-err >&2; exec 0<&-; sleep 0.1"
            (Some(Array.zeroCreate<byte> 1_048_576))
            64
            2000

    Expect.equal (Encoding.UTF8.GetString delivery.StandardOutput) "synthetic-out" "Delivery stdout"
    Expect.equal (Encoding.UTF8.GetString delivery.StandardError) "synthetic-err" "Delivery stderr"

    let completed =
        BoundedProcess.run
            (start "printf synthetic-out; printf synthetic-err >&2; exit 7" false)
            None
            64
            2000

    Expect.equal completed.ExitCode 7 "A completed nonzero child remains a normal result"

    Expect.equal
        (Encoding.UTF8.GetString completed.StandardOutput)
        "synthetic-out"
        "Completed stdout"

    Expect.equal
        (Encoding.UTF8.GetString completed.StandardError)
        "synthetic-err"
        "Completed stderr"

[<Tests>]
let tests =
    testCase
        "bounded child failures retain partial private evidence without exposing output"
        controls
