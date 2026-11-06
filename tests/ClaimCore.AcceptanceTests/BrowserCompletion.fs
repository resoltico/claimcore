module internal ClaimCore.AcceptanceTests.BrowserCompletion

open System
open System.Globalization
open System.IO
open System.Text.Json

let private require condition message =
    if not condition then
        invalidOp ("Browser completion report: " + message)

let private property name (value: JsonElement) = value.GetProperty(name: string)

let private text name value =
    property name value |> _.GetString() |> nonNull

let private items name value =
    property name value |> _.EnumerateArray() |> Seq.toList

let rec private specifications (suite: JsonElement) =
    let children =
        match suite.TryGetProperty "suites" with
        | true, nested -> nested.EnumerateArray() |> Seq.collect specifications |> Seq.toList
        | _ -> []

    items "specs" suite @ children

let private passed engine test =
    require (text "projectName" test = engine) "unexpected project"
    require (text "expectedStatus" test = "passed") "expected failure or skip"
    require (text "status" test = "expected") "incomplete or flaky test"
    let results = items "results" test
    require (results.Length = 1) "missing or repeated execution"
    let result = results.Head
    require (text "status" result = "passed") "test did not pass"
    require ((property "retry" result).GetInt32() = 0) "retried execution"
    require (items "errors" result |> List.isEmpty) "test errors"

let private project engine root =
    let config = property "config" root
    require ((property "forbidOnly" config).GetBoolean()) "focused tests permitted"

    let selected =
        items "projects" config |> List.filter (fun item -> text "name" item = engine)

    require (selected.Length = 1) "missing or duplicate project"
    let selected = selected.Head
    require ((property "retries" selected).GetInt32() = 0) "retries configured"
    require ((property "repeatEach" selected).GetInt32() = 1) "repeated tests configured"
    text "rootDir" config

let inspect expectedFiles engine bytes =
    use document = JsonDocument.Parse(bytes: byte array)
    let root = document.RootElement
    require (items "errors" root |> List.isEmpty) "runner errors"
    let directory = project engine root
    let specs = items "suites" root |> List.collect specifications
    require (not specs.IsEmpty) "no discovered tests"
    let ids = specs |> List.map (text "id")
    require ((Set.ofList ids).Count = ids.Length) "duplicate test identities"

    let actualFiles =
        specs
        |> List.map (fun spec -> Path.GetFullPath(Path.Combine(directory, text "file" spec)))
        |> Set.ofList

    require (actualFiles = expectedFiles) "specification file inventory differs"

    for spec in specs do
        require ((property "ok" spec).GetBoolean()) "unsuccessful specification"
        let tests = items "tests" spec
        require (tests.Length = 1) "missing or duplicate project execution"
        tests |> List.iter (passed engine)

    let stats = property "stats" root
    require ((property "expected" stats).GetInt32() = specs.Length) "execution count differs"

    for name in [ "unexpected"; "flaky"; "skipped" ] do
        require ((property name stats).GetInt32() = 0) "incomplete execution statistics"

    let milliseconds = (property "duration" stats).GetDouble()
    require (Double.IsFinite milliseconds && milliseconds >= 0.) "invalid duration"
    specs.Length, (milliseconds / 1000.).ToString("0.###", CultureInfo.InvariantCulture) + "s"

/// Resolve one literal title from collection or execution using the same report structure.
let selectedCase expectedFiles engine title bytes =
    use document = JsonDocument.Parse(bytes: byte array)
    let root = document.RootElement
    require (items "errors" root |> List.isEmpty) "collection errors"
    let directory = project engine root
    let specs = items "suites" root |> List.collect specifications
    require (specs.Length = 1) "literal case must select exactly one test"
    let spec = specs.Head
    require (text "title" spec = title) "literal case title differs"
    let file = Path.GetFullPath(Path.Combine(directory, text "file" spec))
    require (expectedFiles = Set.singleton file) "literal case file differs"
    let tests = items "tests" spec
    require (tests.Length = 1) "literal case project inventory differs"
    require (text "projectName" tests.Head = engine) "literal case engine differs"

    require
        (text "expectedStatus" tests.Head = "passed")
        "literal case is disabled or expects failure"

    let identity = text "id" spec
    require (not (String.IsNullOrWhiteSpace identity)) "literal case identity is missing"
    identity

let private reportBytes path =
    use stream = File.OpenRead path
    require (stream.Length > 0L && stream.Length <= 8_388_608L) "missing or oversized report"
    let bytes = Array.zeroCreate<byte>(int stream.Length)
    stream.ReadExactly bytes
    bytes

let read expectedFiles engine path =
    try
        inspect expectedFiles engine (reportBytes path)
    with
    | :? JsonException
    | :? Collections.Generic.KeyNotFoundException ->
        invalidOp "Browser completion report has an invalid structure."

let readCase expectedFiles engine title identity path =
    try
        let bytes = reportBytes path
        let result = inspect expectedFiles engine bytes
        require (fst result = 1) "literal case execution count differs"

        require
            (selectedCase expectedFiles engine title bytes = identity)
            "literal case identity differs"

        result
    with
    | :? JsonException
    | :? Collections.Generic.KeyNotFoundException ->
        invalidOp "Browser completion report has an invalid structure."
