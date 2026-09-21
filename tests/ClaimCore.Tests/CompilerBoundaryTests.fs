module ClaimCore.Tests.CompilerBoundaryTests

open System
open System.IO
open System.Text.RegularExpressions
open System.Text.Json
open Expecto
open ClaimCore.TestSupport

let private assemblyDirectory () =
    CliProcessTests.runDotnet
        [
            "msbuild"
            Path.Combine(RepositoryRoot.find (), "src/ClaimCore.Cli/ClaimCore.Cli.fsproj")
            "-nologo"
            "-verbosity:quiet"
            "-property:Configuration=Release"
            "-getProperty:TargetDir"
        ]
        ""
    |> fun result ->
        if result.ExitCode <> 0 || not (String.IsNullOrEmpty(result.StandardError)) then
            failwith $"Could not resolve the CLI output directory. {result.StandardError}"

        result.StandardOutput.Trim()

let private references =
    lazy
        (let directory = assemblyDirectory ()

         [
             "ClaimCore.Domain"
             "ClaimCore.RecordFormat"
             "ClaimCore.Application"
             "ClaimCore.Contracts"
             "ClaimCore.Postgres"
         ]
         |> List.map (fun name ->
             let file = Path.Combine(directory, name + ".dll")

             if not (File.Exists(file)) then
                 failwith $"Required compiler-probe reference is missing: {file}"

             let escaped = file.Replace("\"", "\"\"")
             $"#r @\"{escaped}\"")
         |> String.concat Environment.NewLine)

let private runProbe name source =
    let directory =
        Path.Combine(Path.GetTempPath(), "claimcore-boundary-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory(directory) |> ignore
    let file = Path.Combine(directory, name + ".fsx")

    try
        File.WriteAllText(file, references.Value + Environment.NewLine + source)
        CliProcessTests.runDotnet [ "fsi"; "--nologo"; "--exec"; file ] ""
    finally
        Directory.Delete(directory, true)

let private positive =
    lazy
        (runProbe
            "public-core"
            "open System.Threading\nopen System.Threading.Tasks\nopen ClaimCore.Application\nlet describe (core: IClaimsCore) : CoreDescription = core.Describe()\nlet read (core: IClaimsCore) : Task<QueryOutcome<Lookup<CurrentCase, string>>> = core.Get(\"EXAMPLE\", CancellationToken.None)\nlet fieldCount (description: CoreDescription) = description.Contract.Fields.Length\nprintfn \"CORE_BOUNDARY_POSITIVE\"")

let private requirePositive () =
    let result = positive.Value
    Expect.equal result.ExitCode 0 "Public core caller compiles"
    Expect.stringContains result.StandardOutput "CORE_BOUNDARY_POSITIVE" "Positive marker"

let private inaccessible name symbol source =
    testCase name (fun () ->
        requirePositive ()
        let result = runProbe name source
        let diagnostics = result.StandardOutput + result.StandardError
        Expect.notEqual result.ExitCode 0 "Private symbol must not compile"

        Expect.isTrue
            (Regex.IsMatch(diagnostics, @"error FS(?:0039|1092|1094|0491)\b"))
            "Expected F# accessibility error"

        Expect.stringContains diagnostics symbol "Diagnostic identifies intended symbol")

/// Inspect the actual compiler references, not just the declared ProjectReference graph.
/// Runtime storage dependencies must still be published, but must not be usable by host source.
let private hostCompileClosures () =
    for name in [ "Cli"; "Web" ] do
        let project =
            Path.Combine(RepositoryRoot.find (), $"src/ClaimCore.{name}/ClaimCore.{name}.fsproj")

        let result =
            CliProcessTests.runDotnet
                [
                    "msbuild"
                    project
                    "-nologo"
                    "-verbosity:quiet"
                    "-property:Configuration=Release"
                    "-target:ResolveReferences"
                    "-getItem:ReferencePath"
                ]
                ""

        Expect.equal result.ExitCode 0 "Compiler reference resolution must succeed"
        use document = JsonDocument.Parse(result.StandardOutput)

        let names =
            document.RootElement.GetProperty("Items").GetProperty("ReferencePath").EnumerateArray()
            |> Seq.map (fun value -> Path.GetFileName(value.GetProperty("FullPath").GetString()))
            |> Set.ofSeq

        Expect.isTrue (names.Contains "ClaimCore.Application.dll") "Positive facade reference"

        for forbidden in [ "ClaimCore.Postgres.dll"; "Npgsql.dll" ] do
            Expect.isFalse
                (names.Contains forbidden)
                (name + " cannot compile against " + forbidden)

    for required in [ "ClaimCore.Postgres.dll"; "Npgsql.dll" ] do
        Expect.isTrue
            (File.Exists(Path.Combine(assemblyDirectory (), required)))
            ("Runtime dependency is still delivered: " + required)

let tests =
    testList
        "compiler-enforced core boundary"
        [
            testCase "ordinary caller can use IClaimsCore" requirePositive
            testCase
                "case-work hosts exclude storage from compilation, not deployment"
                hostCompileClosures
            inaccessible
                "ordinary caller cannot see the store port"
                "IClaimStore"
                "let value = typeof<ClaimCore.Application.IClaimStore>"
            inaccessible
                "ordinary caller cannot create the core callback"
                "CoreApi"
                "let value = ClaimCore.Application.CoreApi.create"
            inaccessible
                "ordinary caller cannot instantiate PostgreSQL storage"
                "PostgresStore"
                "let value = typeof<ClaimCore.Postgres.PostgresStore>"
            inaccessible
                "ordinary caller cannot forge a prepared operation"
                "PreparedOperation"
                "let value = typeof<ClaimCore.Application.PreparedOperation>"
        ]
