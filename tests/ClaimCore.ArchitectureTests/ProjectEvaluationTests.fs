module ClaimCore.ArchitectureTests.ProjectEvaluationTests

open System
open System.IO
open System.Security
open Expecto
open ClaimCore.TestSupport

let private conditionalImport () =
    let directory =
        Path.Combine(Path.GetTempPath(), "claimcore-architecture-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory directory |> ignore

    try
        let source = Path.Combine(directory, "ClaimCore.Domain.fsproj")
        let imported = Path.Combine(directory, "edge.props")

        let cli =
            Path.Combine(RepositoryRoot.find (), "src/ClaimCore.Cli/ClaimCore.Cli.fsproj")
            |> SecurityElement.Escape

        File.WriteAllText(
            imported,
            $"<Project><ItemGroup><ProjectReference Include='{cli}' Condition=\"'$(Configuration)' == 'Release'\" />"
            + "<PackageReference Include='Npgsql' Condition=\"'$(Configuration)' == 'Release'\" />"
            + "</ItemGroup></Project>"
        )

        File.WriteAllText(source, "<Project><Import Project='edge.props' /></Project>")

        let projects =
            ProjectReferences.loadProjects (Path.Combine(RepositoryRoot.find (), "src"))
            |> ProjectReferences.names

        let permissions = Map.ofList [ "ClaimCore.Domain", [] ]
        let debug = ProjectEvaluation.evaluate source "Debug"
        let release = ProjectEvaluation.evaluate source "Release"

        Expect.isEmpty
            (ProjectEvaluation.violations projects permissions source debug)
            "The inactive conditional edge must not appear in Debug"

        let failures = ProjectEvaluation.violations projects permissions source release

        Expect.equal failures.Length 2 "Release must detect both imported forbidden edges"

        Expect.isTrue
            (failures |> List.exists (fun failure -> failure.Contains("ClaimCore.Cli")))
            "The imported project edge must identify the forbidden component"

        Expect.isTrue
            (failures |> List.exists (fun failure -> failure.Contains("Npgsql")))
            "The imported package edge must identify the known forbidden dependency"
    finally
        Directory.Delete(directory, true)

let tests =
    testList
        "evaluated project graph qualification"
        [
            testCase
                "imported conditional references fail in the active configuration"
                conditionalImport
        ]
