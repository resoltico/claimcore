namespace ClaimCore.Docs

open System
open System.IO
open System.Text.Json

[<RequireQualifiedAccess>]
module StructuredReports =
    let private exactProperties (expected: Set<string>) (element: JsonElement) =
        if element.ValueKind <> JsonValueKind.Object then
            Error "A structured test report has an unexpected object shape."
        else
            let names = element.EnumerateObject() |> Seq.map _.Name |> Seq.toList
            let actual = Set.ofList names

            if names.Length <> actual.Count then
                Error "A structured test report contains duplicate properties."
            elif actual = expected then
                Ok()
            else
                Error "A structured test report has an unexpected object shape."

    let private text (name: string) (element: JsonElement) =
        let mutable value = JsonElement()

        if
            element.ValueKind = JsonValueKind.Object
            && element.TryGetProperty(name, &value)
            && value.ValueKind = JsonValueKind.String
        then
            match value.GetString() |> Option.ofObj with
            | Some content -> Ok content
            | None -> Error $"Structured report property '{name}' is null."
        else
            Error $"Structured report property '{name}' is not text."

    let private nonNegative (name: string) (element: JsonElement) =
        let mutable property = JsonElement()
        let mutable value = 0

        if
            element.ValueKind = JsonValueKind.Object
            && element.TryGetProperty(name, &property)
            && property.ValueKind = JsonValueKind.Number
            && property.TryGetInt32(&value)
            && value >= 0
        then
            Ok value
        else
            Error $"Structured report counter '{name}' is invalid."

    let private exactInteger (name: string) expected element =
        nonNegative name element
        |> Result.bind (fun value ->
            if value = expected then
                Ok()
            else
                Error $"Structured report property '{name}' is invalid.")

    let private emptyArray (name: string) (element: JsonElement) =
        let mutable value = JsonElement()

        if
            element.ValueKind = JsonValueKind.Object
            && element.TryGetProperty(name, &value)
            && value.ValueKind = JsonValueKind.Array
            && value.GetArrayLength() = 0
        then
            Ok()
        else
            Error $"Structured report property '{name}' must be an empty array."

    let private testIdentities (expected: Set<string>) (items: JsonElement) =
        if items.ValueKind <> JsonValueKind.Array then
            Error "Structured report tests must be an array."
        else
            let parsed =
                items.EnumerateArray()
                |> Seq.map (fun item ->
                    exactProperties (set [ "id"; "outcome"; "durationMs" ]) item
                    |> Result.bind (fun () ->
                        match
                            text "id" item, text "outcome" item, nonNegative "durationMs" item
                        with
                        | Ok id, Ok "passed", Ok _ when id.Length > 0 && id.Length <= 240 -> Ok id
                        | Ok _, Ok _, Ok _ -> Error "A structured test did not pass."
                        | Error message, _, _
                        | _, Error message, _
                        | _, _, Error message -> Error message))
                |> Seq.toList

            match parsed |> List.tryPick Result.toOption with
            | Some _ ->
                let failures =
                    parsed
                    |> List.choose (function
                        | Error error -> Some error
                        | _ -> None)

                if failures.IsEmpty then
                    let ids = parsed |> List.choose Result.toOption

                    if ids.Length <> (ids |> Set.ofList |> Set.count) then
                        Error "Structured report test identities are duplicated."
                    elif Set.ofList ids <> expected then
                        Error "Structured report test identities differ from the compiled registry."
                    else
                        Ok()
                else
                    Error(List.head failures)
            | None -> Error "Structured report contains no passing tests."

    let private totals expectedPassed (failureNames: string list) (element: JsonElement) =
        exactProperties (Set.ofList ("passed" :: failureNames)) element
        |> Result.bind (fun () ->
            match nonNegative "passed" element with
            | Error message -> Error message
            | Ok passed when passed <> expectedPassed ->
                Error "Structured report pass count differs."
            | Ok _ ->
                failureNames
                |> List.map (fun name -> nonNegative name element)
                |> List.tryPick (function
                    | Ok 0 -> None
                    | Ok _ -> Some "Structured report has non-passing outcomes."
                    | Error error -> Some error)
                |> function
                    | None -> Ok()
                    | Some error -> Error error)

    let private reportPath root (stage: StageManifest) (relative: string) =
        match Repository.registeredPath root stage.OutputRoot with
        | Error message -> Error message
        | Ok outputRoot ->
            let matches = stage.Output.Files |> List.filter (fun file -> file.Path = relative)

            match matches with
            | [ file ] ->
                Repository.ensureExistingSafe root (Path.Combine(outputRoot, relative))
                |> Result.bind (fun path ->
                    if Repository.sha256File path = file.Sha256 then
                        Ok path
                    else
                        Error "Structured report bytes differ from their producer manifest.")
            | _ -> Error $"Stage '{stage.StageId}' lacks exactly one structured report."

    let private document root stage relative =
        reportPath root stage relative
        |> Result.bind (fun path ->
            try
                let info = FileInfo(path)

                if info.Length > 16L * 1024L * 1024L then
                    Error "Structured test report exceeds its bounded size."
                else
                    Ok(JsonDocument.Parse(File.ReadAllBytes(path)))
            with error ->
                Error(error.GetType().Name + ": " + error.Message))

    let private validateVitestElement (value: JsonElement) =
        let expected = FrontendTestCatalog.vitest.Count

        exactProperties (set [ "format"; "formatVersion"; "status"; "totals"; "tests" ]) value
        |> Result.bind (fun () ->
            match
                text "format" value, text "status" value, exactInteger "formatVersion" 1 value
            with
            | Ok "claimcore-vitest-report", Ok "passed", Ok() ->
                totals expected [ "failed"; "skipped"; "todo" ] (value.GetProperty("totals"))
                |> Result.bind (fun () ->
                    testIdentities FrontendTestCatalog.vitest (value.GetProperty("tests")))
            | _ -> Error "Vitest report identity or status is invalid.")

    let internal validateVitestBytes (bytes: byte array) =
        try
            use report = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
            validateVitestElement report.RootElement
        with error ->
            Error(error.GetType().Name + ": " + error.Message)

    let private vitest root stage =
        match document root stage "vitest-summary.json" with
        | Error message -> Error message
        | Ok report ->
            use owned = report
            validateVitestElement owned.RootElement

    let private validateBrowserElement engine (value: JsonElement) =
        let expected = FrontendTestCatalog.browser.Count

        exactProperties
            (set
                [
                    "format"
                    "formatVersion"
                    "scope"
                    "status"
                    "expected"
                    "totals"
                    "tests"
                    "failureLines"
                    "failureCodes"
                ])
            value
        |> Result.bind (fun () ->
            match
                text "format" value,
                text "scope" value,
                text "status" value,
                exactInteger "formatVersion" 1 value,
                exactInteger "expected" expected value
            with
            | Ok "claimcore-playwright-report", Ok scope, Ok "passed", Ok(), Ok() when
                scope = engine
                ->
                totals
                    expected
                    [ "failed"; "skipped"; "timedOut"; "interrupted" ]
                    (value.GetProperty("totals"))
                |> Result.bind (fun () ->
                    testIdentities FrontendTestCatalog.browser (value.GetProperty("tests")))
                |> Result.bind (fun () ->
                    emptyArray "failureLines" value
                    |> Result.bind (fun () -> emptyArray "failureCodes" value))
            | _ -> Error "Browser report identity or status is invalid.")

    let internal validateBrowserBytes engine (bytes: byte array) =
        try
            use report = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
            validateBrowserElement engine report.RootElement
        with error ->
            Error(error.GetType().Name + ": " + error.Message)

    let private browser root engine stage =
        match document root stage (engine + ".json") with
        | Error message -> Error message
        | Ok report ->
            use owned = report
            validateBrowserElement engine owned.RootElement

    let validate (root: RepositoryRoot) (stages: StageManifest list) =
        let find id =
            stages |> List.tryFind (fun stage -> stage.StageId = id)

        match
            find "frontend-unit",
            find "browser-chromium",
            find "browser-firefox",
            find "browser-webkit"
        with
        | Some frontend, Some chromium, Some firefox, Some webkit ->
            [
                vitest root frontend
                browser root "chromium" chromium
                browser root "firefox" firefox
                browser root "webkit" webkit
            ]
            |> List.tryPick (function
                | Error error -> Some error
                | Ok() -> None)
            |> function
                | None -> Ok()
                | Some error -> Error error
        | _ -> Error "Structured-report producer stages are incomplete."
