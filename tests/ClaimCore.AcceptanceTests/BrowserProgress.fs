module ClaimCore.AcceptanceTests.BrowserProgress

open System.Text
open System.Text.RegularExpressions

let private completedRow =
    Regex(
        @"(?m)^[ \t]*(✓|✘)[ \t]+([1-9][0-9]*)[ \t]+\[(chromium|firefox|webkit)\][ \t]+›[ \t]+e2e/(?:[a-zA-Z0-9_-]+/)*(?<file>[a-zA-Z0-9_-]{1,100}\.spec\.[jt]sx?):(?<line>[1-9][0-9]{0,6}):[1-9][0-9]{0,6}[ \t]+›[ \t]+[^\r\n]+[ \t]+\([0-9]+(?:\.[0-9]+)?(?:ms|s|m)\)[ \t]*\r?$",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )

/// Observed list-reporter completions aid diagnosis; they never satisfy qualification.
let private rows bytes =
    let plain =
        Regex.Replace(Encoding.UTF8.GetString(bytes: byte array), "\\x1B\\[[0-?]*[ -/]*[@-~]", "")

    completedRow.Matches plain

let counts bytes =
    let rows =
        rows bytes
        |> Seq.map (fun row -> row.Groups[1].Value, row.Groups[2].Value, row.Groups[3].Value)
        |> Set.ofSeq

    let passed = rows |> Set.filter (fun (result, _, _) -> result = "✓") |> Set.count
    let failed = rows |> Set.filter (fun (result, _, _) -> result = "✘") |> Set.count
    passed, failed

/// Only recognized code basenames/positive lines are public; titles and details stay private.
let failedLocations bytes =
    rows bytes
    |> Seq.filter (fun row -> row.Groups[1].Value = "✘")
    |> Seq.map (fun row -> row.Groups["file"].Value + ":" + row.Groups["line"].Value)
    |> Set.ofSeq
    |> Seq.truncate 5
    |> Seq.toList
