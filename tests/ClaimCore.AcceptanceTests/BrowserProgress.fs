module ClaimCore.AcceptanceTests.BrowserProgress

open System.Text
open ClaimCore.Protocol
open System.Text.RegularExpressions
open System.IO

let private completedRow =
    Regex(
        @"(?m)^[ \t]*(✓|✘)[ \t]+([1-9][0-9]*)[ \t]+\[(chromium|firefox|webkit)\][ \t]+›[ \t]+e2e/(?:[a-zA-Z0-9_-]+/)*(?<file>[a-zA-Z0-9_-]{1,100}\.spec\.[jt]sx?):(?<line>[1-9][0-9]{0,6}):[1-9][0-9]{0,6}[ \t]+›[ \t]+[^\r\n]+[ \t]+\((?<duration>[0-9]{1,6}(?:\.[0-9]{1,3})?(?:ms|s|m))\)[ \t]*\r?$",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )

/// Observed list-reporter completions aid diagnosis; they never satisfy qualification.
let private plain bytes =
    Regex.Replace(Encoding.UTF8.GetString(bytes: byte array), "\\x1B\\[[0-?]*[ -/]*[@-~]", "")

let private rows bytes = completedRow.Matches(plain bytes)

let private codePaths (codeFiles: string seq) =
    codeFiles
    |> Seq.filter (fun path ->
        Regex.IsMatch(
            Path.GetFileName path |> nonNull,
            @"\A[a-zA-Z0-9_-]{1,100}(?:\.spec)?\.[jt]sx?\z",
            RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
        ))
    |> Set.ofSeq

let counts bytes =
    let rows =
        rows bytes
        |> Seq.map (fun row -> row.Groups[1].Value, row.Groups[2].Value, row.Groups[3].Value)
        |> Set.ofSeq

    let passed = rows |> Set.filter (fun (result, _, _) -> result = "✓") |> Set.count
    let failed = rows |> Set.filter (fun (result, _, _) -> result = "✘") |> Set.count
    passed, failed

let private failedRows codeFiles bytes =
    let allowed =
        codePaths codeFiles
        |> Seq.map (fun path -> Path.GetFileName path |> nonNull)
        |> Set.ofSeq

    rows bytes
    |> Seq.filter (fun row -> row.Groups[1].Value = "✘")
    |> Seq.filter (fun row -> Set.contains row.Groups["file"].Value allowed)

/// Only recognized code basenames/positive lines are public; titles and details stay private.
let failedLocations codeFiles bytes =
    failedRows codeFiles bytes
    |> Seq.map (fun row -> row.Groups["file"].Value + ":" + row.Groups["line"].Value)
    |> Set.ofSeq
    |> Seq.truncate 5
    |> Seq.toList

let failedDurations codeFiles bytes =
    failedRows codeFiles bytes
    |> Seq.map (fun row ->
        row.Groups["file"].Value
        + ":"
        + row.Groups["line"].Value
        + ":"
        + row.Groups["duration"].Value)
    |> Set.ofSeq
    |> Seq.truncate 5
    |> Seq.toList

let private stackRow =
    let location = @"(?<path>/[^\r\n():]+):(?<line>[1-9][0-9]{0,6}):[1-9][0-9]{0,6}"

    Regex(
        @"(?m)^[ \t]+at (?:"
        + location
        + @"|[^\r\n()]{1,200} \("
        + location
        + @"\))[ \t]*\r?$",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )

/// Public frames require an exact known source path; only its basename and line survive.
let stackLocations codeFiles bytes =
    let allowed = codePaths codeFiles

    stackRow.Matches(plain bytes)
    |> Seq.filter (fun row -> Set.contains row.Groups["path"].Value allowed)
    |> Seq.map (fun row ->
        (Path.GetFileName(row.Groups["path"].Value) |> nonNull)
        + ":"
        + row.Groups["line"].Value)
    |> Seq.distinct
    |> Seq.truncate 5
    |> Seq.toList

let private submitFailures =
    HostFailureReason.all
    |> List.map (fun reason ->
        let phase =
            HostFailureReason.phase reason
            |> Option.map ExecutionPhase.phase
            |> Option.defaultValue "NONE"

        $"{HostFailureReason.status reason}_{HostFailureReason.code reason}_{phase}"
        |> Regex.Escape)
    |> String.concat "|"
    |> fun known -> known + "|[1-5][0-9]{2}_UNCLASSIFIED_NONE"

let private readHostFailures =
    HostFailureReason.all
    |> List.map (fun reason -> $"{HostFailureReason.status reason}_{HostFailureReason.code reason}")
    |> Set.ofList

let private sessionOutcomeFailures =
    DiagnosticCodecs.codes @ (DiagnosticCodecs.faultDefinitions |> List.map _.Id)
    |> List.map (fun code -> "200_" + code)
    |> Set.ofList

let private readFailureCategory kind (code: string) =
    // The bounded marker grammar has already checked the three-digit status and separator.
    let detail = code.Substring 4

    let known =
        Set.contains code readHostFailures
        || (kind = "SESSION"
            && (Set.contains code sessionOutcomeFailures
                || List.contains
                    detail
                    [ "INVALID_WIRE"; "UNEXPECTED_STATUS"; "MISSING_ANTIFORGERY" ]))
        || (kind = "WORK_READBACK"
            && List.contains detail [ "INVALID_RESPONSE"; "RAW_ISSUER_PREVIEW" ])

    if known then kind + "_HTTP_" + code else "ERROR"

let private factReadabilityCode =
    "(?:INCIDENT_DATE|INCIDENT_NOTIFICATION_DATE|PAYMENT_DECISION_DATE|PAYMENT_DATE)_(?:CASES_MINE|CASES_NOTIFIED|CASES_SUMMARY|CASES_OPEN_UNDECIDED|CASES_DECIDED_UNPAID|CASES_RECENT|CASES_CLOSED|CASES_VOIDED|CASES_ALL|CASES_FILTERED|CASE_UNDECIDED|CASE_DECIDED|CASE_PAID|CASE_CLOSED|CASE_VOIDED|CASE_ERASED|EDITOR_OPEN|EDITOR_DECIDE|EDITOR_CORRECT|EDITOR_VOID|EDITOR_WORK|RECOVERY|PAID_CORRECTION_REVIEW|HISTORY_COMPARISON|OPERATION_RESULT|AUTHORITY_HISTORY|NOT_FOUND|AUTHORITY_LIST|AUTHORITY_DETAIL|CASES_FILTER_PANEL)_(?:EXPOSED|INERT)"

/// Only closed field, scene and visibility tokens cross into safe diagnostics.
let factReadabilityMarkers bytes =
    Regex.Matches(
        plain bytes,
        @"(?m)^[ \t]*(?:Error: )?E2E_FACT_READABILITY_(?<code>"
        + factReadabilityCode
        + @")[ \t]*\r?$",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )
    |> Seq.map (fun row -> row.Groups["code"].Value)
    |> Set.ofSeq
    |> Set.toList

/// Categories are a closed vocabulary; no error message or stack function is returned.
let failureCategories bytes =
    Regex.Matches(
        plain bytes,
        @"(?m)^[ \t]+(?:(?<fact>(?:Error: )?E2E_FACT_READABILITY_"
        + factReadabilityCode
        + @")[ \t]*\r?$|(?<accessibility>Error: E2E_AXE_[A-Z0-9_]{1,200}[ \t]*\r?$)|(?<layout>Error: E2E_LAYOUT_(?:LOGIN|CASES|NARROW|ZOOM)_OVERFLOW(?:_REPORT_UNAVAILABLE)?[ \t]*\r?$)|(?<assertion>AssertionError:|Error: expect\()|(?<testTimeout>(?:Error: (?:locator|page)\.[a-zA-Z][a-zA-Z0-9]{0,39}: )?Test timeout of [1-9][0-9]{0,7}ms exceeded\.)|(?<actionTimeout>TimeoutError:|Error: (?:locator|page)\.[a-zA-Z][a-zA-Z0-9]{0,39}: Timeout [1-9][0-9]{0,7}ms exceeded\.)|(?<timeout>(?:Error: )?Timeout [1-9][0-9]{0,7}ms exceeded\.)|(?<submit>Error: E2E_SUBMIT_HTTP_(?<submitCode>"
        + submitFailures
        + @")[ \t]*\r?$)|(?<read>Error: E2E_(?<readKind>SESSION|WORK_READBACK)_HTTP_(?<readCode>[1-5][0-9]{2}_[A-Z_]{1,80})[ \t]*\r?$)|(?<globalTimeout>Timed out waiting [1-9][0-9]{0,8}ms for the entire test run\.?[ \t]*\r?$)|(?<error>Error:))",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )
    |> Seq.map (fun row ->
        if row.Groups["fact"].Success then
            "FACT_READABILITY"
        elif row.Groups["submit"].Success then
            "HTTP_" + row.Groups["submitCode"].Value
        elif row.Groups["read"].Success then
            readFailureCategory row.Groups["readKind"].Value row.Groups["readCode"].Value
        elif row.Groups["globalTimeout"].Success then
            "GLOBAL_TIMEOUT"
        elif row.Groups["accessibility"].Success then
            "ACCESSIBILITY"
        elif row.Groups["layout"].Success then
            "LAYOUT"
        elif row.Groups["assertion"].Success then
            "ASSERTION"
        elif row.Groups["testTimeout"].Success then
            "TEST_TIMEOUT"
        elif row.Groups["actionTimeout"].Success then
            "ACTION_TIMEOUT"
        elif row.Groups["timeout"].Success then
            "TIMEOUT"
        else
            "ERROR")
    |> Set.ofSeq
    |> Set.toList

/// Only fixed tags and bounded decimal geometry cross into public failure summaries.
let targetGeometry bytes =
    let dimension = @"(?:0|[1-9][0-9]{0,3})\.[0-9]{2}"

    Regex.Matches(
        plain bytes,
        @"(?m)^[ \t]*(?:Error: )?E2E_TARGET_GEOMETRY tag=(?<tag>BUTTON|A|INPUT|SELECT|TEXTAREA|SUMMARY|DIV|SPAN|OTHER) width=(?<width>"
        + dimension
        + @") height=(?<height>"
        + dimension
        + @") min=(?<minimum>24|44)[ \t]*\r?$",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )
    |> Seq.map (fun row ->
        row.Groups["tag"].Value
        + ":"
        + row.Groups["width"].Value
        + "x"
        + row.Groups["height"].Value
        + ":min"
        + row.Groups["minimum"].Value)
    |> Seq.distinct
    |> Seq.truncate 5
    |> Seq.toList
