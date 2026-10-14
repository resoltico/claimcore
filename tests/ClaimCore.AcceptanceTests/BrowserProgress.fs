module ClaimCore.AcceptanceTests.BrowserProgress

open System.Text
open System.Text.RegularExpressions

let private completedRow =
    Regex(
        @"(?m)^[ \t]*(✓|✘)[ \t]+([1-9][0-9]*)[ \t]+\[(chromium|firefox|webkit)\][ \t]+›[ \t]+e2e/[a-zA-Z0-9_./-]+\.spec\.[jt]sx?:[1-9][0-9]*:[1-9][0-9]*[ \t]+›[ \t]+[^\r\n]+[ \t]+\([0-9]+(?:\.[0-9]+)?(?:ms|s|m)\)[ \t]*\r?$",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )

/// Observed list-reporter completions aid diagnosis; they never satisfy qualification.
let counts bytes =
    let plain =
        Regex.Replace(Encoding.UTF8.GetString(bytes: byte array), "\\x1B\\[[0-?]*[ -/]*[@-~]", "")

    let rows =
        completedRow.Matches plain
        |> Seq.map (fun row -> row.Groups[1].Value, row.Groups[2].Value, row.Groups[3].Value)
        |> Set.ofSeq

    let passed = rows |> Set.filter (fun (result, _, _) -> result = "✓") |> Set.count
    let failed = rows |> Set.filter (fun (result, _, _) -> result = "✘") |> Set.count
    passed, failed
