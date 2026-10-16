module ClaimCore.AcceptanceTests.BrowserProgress

open System.Text
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

/// Categories are a closed vocabulary; no error message or stack function is returned.
let failureCategories bytes =
    Regex.Matches(
        plain bytes,
        @"(?m)^[ \t]+(?:(?<accessibility>Error: E2E_AXE_[A-Z0-9_]{1,200}[ \t]*\r?$)|(?<assertion>AssertionError:|Error: expect\()|(?<testTimeout>(?:Error: (?:locator|page)\.[a-zA-Z][a-zA-Z0-9]{0,39}: )?Test timeout of [1-9][0-9]{0,7}ms exceeded\.)|(?<actionTimeout>TimeoutError:|Error: (?:locator|page)\.[a-zA-Z][a-zA-Z0-9]{0,39}: Timeout [1-9][0-9]{0,7}ms exceeded\.)|(?<timeout>(?:Error: )?Timeout [1-9][0-9]{0,7}ms exceeded\.)|(?<error>Error:))",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )
    |> Seq.map (fun row ->
        if row.Groups["accessibility"].Success then
            "ACCESSIBILITY"
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

/// Refresh observations contain only fixed kinds/statuses and generated public diagnostic IDs.
let authorityReads diagnosticIds bytes =
    let known = diagnosticIds |> Set.ofSeq

    Regex.Matches(
        plain bytes,
        @"(?m)^[ \t]*E2E_AUTHORITY_READ (?<endpoint>CONTEXT|LIST) (?<status>0|[1-5][0-9]{2}) (?<kind>SUCCEEDED|REJECTED|FAILED|CANCELLED|HOST_FAILURE|INVALID|UNOBSERVED|PENDING|TRANSPORT) (?<diagnostic>[A-Z][A-Z0-9_]{0,99})[ \t]*\r?$",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )
    |> Seq.choose (fun row ->
        let endpoint = row.Groups["endpoint"].Value
        let status = row.Groups["status"].Value
        let kind = row.Groups["kind"].Value
        let diagnostic = row.Groups["diagnostic"].Value
        let noResponse = List.contains kind [ "UNOBSERVED"; "PENDING"; "TRANSPORT" ]

        if status = "0" && not noResponse then
            None
        elif status <> "0" && List.contains kind [ "UNOBSERVED"; "TRANSPORT" ] then
            None
        else
            let safeDiagnostic =
                if diagnostic = "NONE" || Set.contains diagnostic known then
                    diagnostic
                else
                    "OTHER"

            Some(endpoint, $"{endpoint}:{status}:{kind}:{safeDiagnostic}"))
    |> Seq.fold (fun summary (endpoint, value) -> Map.add endpoint value summary) Map.empty
    |> Map.toList
    |> List.map snd

/// UI phases use fixed observer vocabulary; arbitrary DOM text is never returned.
let authorityPhase bytes =
    Regex.Matches(
        plain bytes,
        @"(?m)^[ \t]*E2E_AUTHORITY_PHASE (?<phase>REVIEW|LOADING|EMPTY_ADVICE|FAILURE|USABLE_PICKER|DISCARD_CONFIRMATION|UNAVAILABLE)[ \t]*\r?$",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )
    |> Seq.map (fun row -> row.Groups["phase"].Value)
    |> Seq.tryLast
    |> Option.defaultValue "UNAVAILABLE"

let authorityBodyTiming bytes =
    Regex.Matches(
        plain bytes,
        @"(?m)^[ \t]*E2E_AUTHORITY_BODY_TIMING (?<stage>OPEN_CASE|REGISTER_ACTOR|CASE_GRANT|DISABLE_ACTOR|NEXT_GRANT|OWNER_REFUSAL|ACCESSIBILITY|FAILURE) (?<elapsed>0|[1-9][0-9]{0,6}) (?<budget>0|[1-9][0-9]{0,6})[ \t]*\r?$",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )
    |> Seq.map (fun row ->
        let stage = row.Groups["stage"].Value
        stage, stage + ":" + row.Groups["elapsed"].Value + "/" + row.Groups["budget"].Value)
    |> Seq.fold (fun summary (stage, value) -> Map.add stage value summary) Map.empty
    |> Map.toList
    |> List.map snd

let authorityAdvice bytes =
    Regex.Matches(
        plain bytes,
        @"(?m)^[ \t]*E2E_AUTHORITY_ADVICE (?<advice>GRANT|NO_GRANT|UNOBSERVED) (?<count>0|[1-9][0-9]{0,2})[ \t]*\r?$",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )
    |> Seq.filter (fun row ->
        let advice = row.Groups["advice"].Value
        let count = row.Groups["count"].Value
        (advice <> "UNOBSERVED" || count = "0") && (advice <> "GRANT" || count <> "0"))
    |> Seq.map (fun row -> row.Groups["advice"].Value + ":" + row.Groups["count"].Value)
    |> Seq.tryLast
    |> Option.defaultValue "UNOBSERVED:0"

let authorityPicker bytes =
    Regex.Matches(
        plain bytes,
        @"(?m)^[ \t]*E2E_AUTHORITY_PICKER (?<state>ENABLED|DISABLED|MISSING|UNOBSERVED) (?<grant>GRANT|NO_GRANT|UNOBSERVED)[ \t]*\r?$",
        RegexOptions.CultureInvariant ||| RegexOptions.NonBacktracking
    )
    |> Seq.map (fun row -> row.Groups["state"].Value + ":" + row.Groups["grant"].Value)
    |> Seq.tryLast
    |> Option.defaultValue "UNOBSERVED:UNOBSERVED"
