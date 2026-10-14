module ClaimCore.Tests.DocumentationDiscovery

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Xml.Linq
open ClaimCore.TestSupport

let private requireNames names =
    if List.isEmpty names || List.exists String.IsNullOrWhiteSpace names then
        invalidOp "Test discovery must produce nonempty test names."

    names

let nativeNames (output: string) =
    let summary =
        Regex.Match(output, @"(?m)^Discovered ([0-9]+) tests in assembly - [^\r\n]+\r?$")

    if not summary.Success then
        invalidOp "Native discovery did not report its test count."

    let names =
        output.Split('\n')
        |> Array.filter (fun line -> line.StartsWith("  ", StringComparison.Ordinal))
        |> Array.map _.Trim()
        |> Array.toList
        |> requireNames

    if names.Length <> Int32.Parse(summary.Groups[1].Value) then
        invalidOp "Native discovery names do not equal its reported count."

    names

let private name (value: JsonElement) (property: string) =
    let text = value.GetProperty(property).GetString() |> nonNull

    if String.IsNullOrWhiteSpace text then
        invalidOp "Test discovery contains an empty name."

    text

let vitestNames bytes =
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))

    document.RootElement.EnumerateArray()
    |> Seq.map (fun item -> name item "name")
    |> Seq.toList
    |> requireNames

let rec private browserSuite parents (suite: JsonElement) =
    let labels = parents @ [ name suite "title" ]

    let cases =
        suite.GetProperty("specs").EnumerateArray()
        |> Seq.collect (fun spec ->
            let title = name spec "title"
            let tests = spec.GetProperty("tests").EnumerateArray() |> Seq.toList

            if List.isEmpty tests then
                invalidOp "Browser discovery contains a spec without engine cases."

            tests
            |> List.map (fun item ->
                String.concat "." (labels @ [ title; name item "projectName" ])))
        |> Seq.toList

    let children =
        match suite.TryGetProperty "suites" with
        | true, nested -> nested.EnumerateArray() |> Seq.collect (browserSuite labels) |> Seq.toList
        | false, _ -> []

    cases @ children

let playwrightNames bytes =
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
    let root = document.RootElement

    if root.GetProperty("errors").GetArrayLength() <> 0 then
        invalidOp "Browser test discovery reported import or configuration errors."

    root.GetProperty("suites").EnumerateArray()
    |> Seq.collect (browserSuite [])
    |> Seq.toList
    |> requireNames

let projects root (solution: XDocument) =
    let paths =
        solution.Descendants(XName.Get "Project")
        |> Seq.map (fun item -> TestGuards.requiredAttribute "Path" item)
        |> Seq.filter (fun path ->
            (Path.GetFileNameWithoutExtension path |> nonNull)
                .EndsWith("Tests", StringComparison.Ordinal))
        |> Seq.map (fun path -> Path.GetFullPath(Path.Combine(root, path)))
        |> Seq.toList
        |> requireNames

    if (Set.ofList paths).Count <> paths.Length then
        invalidOp "The solution repeats a test project."

    paths

let successfulOutput executable (result: BoundedProcessResult) =
    if result.ExitCode <> 0 then
        invalidOp (executable + " test-name discovery failed; no tests were executed.")

    result.StandardOutput

let reportBytes path =
    if FileInfo(path).Length > 1_048_576L then
        invalidOp "Frontend discovery report exceeded its allowance."

    File.ReadAllBytes path

let private run directory executable arguments configure =
    let start =
        ProcessStartInfo(
            executable,
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        )

    CliProcessEnvironment.clearInherited start
    start.Environment["NO_COLOR"] <- "1"
    start.Environment.Remove("FORCE_COLOR") |> ignore
    arguments |> List.iter start.ArgumentList.Add
    configure start
    let result = BoundedProcess.run start None 1_048_576 120_000

    successfulOutput executable result

let private native root configuration project =
    run
        root
        "dotnet"
        [
            "test"
            "--project"
            project
            "--configuration"
            configuration
            "--no-build"
            "--no-restore"
            "--list-tests"
        ]
        ignore
    |> Encoding.UTF8.GetString
    |> nativeNames

let private frontend root =
    let directory =
        Path.Combine(Path.GetTempPath(), "claimcore-test-discovery-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory directory |> ignore

    try
        File.SetUnixFileMode(
            directory,
            UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        )

        let report = Path.Combine(directory, "vitest.json")
        let web = Path.Combine(root, "web")

        run web "npm" [ "exec"; "--"; "vitest"; "list"; "--json=" + report ] ignore
        |> ignore

        let unit = reportBytes report |> vitestNames

        let browser =
            run
                web
                "npm"
                [ "exec"; "--"; "playwright"; "test"; "--list"; "--reporter=json" ]
                (fun start ->
                    start.Environment["CLAIMCORE_TEST_OIDC_CREDENTIALS"] <-
                        Path.Combine(directory, "not-loaded.json"))
            |> playwrightNames

        unit @ browser
    finally
        Directory.Delete(directory, true)

let repository root =
    let configuration =
        match
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()
        with
        | null -> invalidOp "The test assembly must declare its build configuration."
        | attribute -> attribute.Configuration

    let nativeTests =
        XDocument.Load(Path.Combine(root, "ClaimCore.slnx"))
        |> projects root
        |> List.collect (native root configuration)

    nativeTests @ frontend root
