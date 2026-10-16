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
        Encoding.UTF8.GetBytes(value: string)
        |> BrowserProgress.failedLocations [ "/repo/web/e2e/localization-editor.spec.ts" ]

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
            row.Replace("localization-editor.spec.ts", "private-credential.spec.ts")
        ] do
        Expect.isEmpty (locations changed) "Private or malformed locations are omitted"

    Expect.equal
        (locations (
            row.Replace("private payload/title", "https://private.invalid/token?secret=value")
        ))
        [ "localization-editor.spec.ts:58" ]
        "Even reporter titles containing private details are never exposed"

let private stackControls () =
    let files =
        [
            "/repo/web/e2e/session-helpers.ts"
            "/repo/web/e2e/stewardship-authority.spec.ts"
        ]

    let frame =
        "        at submitAuthority (/repo/web/e2e/stewardship-authority.spec.ts:66:3)"

    let locations text =
        Encoding.UTF8.GetBytes(text: string) |> BrowserProgress.stackLocations files

    Expect.equal
        (locations (frame + "\n" + frame + "\n        at /repo/web/e2e/session-helpers.ts:108:9"))
        [ "stewardship-authority.spec.ts:66"; "session-helpers.ts:108" ]
        "Nested helper and bare frames expose only unique source locations"

    Expect.equal
        (locations ("\u001b[31m" + frame + "\u001b[0m"))
        (locations frame)
        "Colors are omitted"

    for changed in
        [
            "private payload " + frame
            frame.Replace("/repo/", "/private/")
            frame.Replace("/e2e/", "/e2e/../e2e/")
            frame.Replace(".spec.ts", ".spec.ts?token=private")
            frame.Replace(":66:3", ":0:3")
            frame.Replace(":66:3", ":66:0")
            frame.Replace("stewardship-authority.spec.ts", "private-payload.spec.ts")
            frame.Replace("submitAuthority (", "private\n(")
        ] do
        Expect.isEmpty
            (locations changed)
            "Unknown, malformed or private stack locations are omitted"

    let many =
        [ 1..8 ]
        |> List.map (fun line -> frame.Replace(":66:3", $":{line}:3"))
        |> String.concat "\n"

    Expect.equal (locations many |> List.length) 5 "Public stack is bounded"

let private categoryControls () =
    let categories text =
        Encoding.UTF8.GetBytes(text: string) |> BrowserProgress.failureCategories

    let privateDetails =
        "private-token https://private.invalid/credential claimant-body"

    let errors =
        [
            "    Error: expect(locator).toHaveText(" + privateDetails + ") failed"
            "    Error: E2E_AXE_LANDMARK_ONE_MAIN__REGION"
            "    TimeoutError: " + privateDetails
            "    Error: " + privateDetails
            "    AssertionError: " + privateDetails
        ]
        |> String.concat "\n"

    Expect.equal
        (categories errors)
        [ "ACCESSIBILITY"; "ASSERTION"; "ERROR"; "TIMEOUT" ]
        "Only closed categories survive private assertion, accessibility and timeout details"

    Expect.equal
        (categories ("payload Error: " + privateDetails))
        []
        "Unrelated text is not an error"

    Expect.equal
        (categories "    Error: E2E_AXE_RULE private-token")
        [ "ERROR" ]
        "Marker requires a safe suffix"

    Expect.equal
        (categories "    Test timeout of 30000ms exceeded.")
        [ "TIMEOUT" ]
        "Runner deadline category"

let private controls () =
    cleanupControls ()
    progressControls ()
    locationControls ()
    stackControls ()
    categoryControls ()

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
