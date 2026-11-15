module ClaimCore.Tests.Architecture.ArchitectureTests

open ClaimCore.CommandLine

open System
open System.IO
open System.Reflection
open System.Diagnostics
open System.Runtime.InteropServices
open System.Text
open System.Text.Json
open ClaimCore.TestSupport
open Expecto
open ArchUnitNET.Loader
open type ArchUnitNET.Fluent.ArchRuleDefinition
open ClaimCore.Tests.Architecture.NegativeFixtures
open ClaimCore.Tests.Architecture.ApiRules

let private root = ClaimCore.TestSupport.RepositoryRoot.find ()

let private projectPaths =
    lazy
        (Directory.EnumerateFiles(
            Path.Combine(root, "src"),
            "*.fsproj",
            SearchOption.AllDirectories
         )
         |> Seq.toArray)

let private products =
    lazy
        (projectPaths.Value
         |> Array.map (fun path ->
             let document = System.Xml.Linq.XDocument.Load path

             let name =
                 document.Descendants(System.Xml.Linq.XName.Get "AssemblyName")
                 |> Seq.tryHead
                 |> Option.map _.Value
                 |> Option.defaultValue (Path.GetFileNameWithoutExtension path)

             Assembly.Load name))

let private model =
    lazy
        (ArchLoader()
            .LoadAssemblies(
                Array.append
                    products.Value
                    [|
                        typeof<ClockCall>.Assembly
                        typeof<Guid>.Assembly
                        typeof<System.Net.Http.HttpClient>.Assembly
                        typeof<System.Text.Json.JsonSerializer>.Assembly
                    |]
            )
            .Build())

let private check rule (assembly: Assembly) =
    ApiRules.check
        model.Value
        rule
        (assembly.GetName().Name |> nonNull)
        (ApiRules.subjects assembly)

let private rejects rule (fixture: System.Type) =
    let subjects =
        ApiRules.subjects fixture.Assembly
        |> fun selected -> selected.And().HaveFullName(fixture.FullName |> nonNull)

    Expect.isFalse
        (subjects.GetObjects model.Value |> Seq.isEmpty)
        "The compiled negative fixture must be present"

    Expect.isFalse
        ((ApiRules.rule model.Value rule subjects).HasNoViolations model.Value)
        "The rule detects a compiled violation, rather than only throwing"

    Expect.throwsT<InvalidOperationException>
        (fun () -> ApiRules.check model.Value rule fixture.Name subjects)
        "The compiled prohibited dependency must be detected"

let private ambientEffects () =
    for name in
        [
            "ClaimCore.Json"
            "ClaimCore.Wire"
            "ClaimCore.Domain"
            "ClaimCore.Application.Contracts"
            "ClaimCore.RecordFormat"
            "ClaimCore.Application"
        ] do
        let assembly = Assembly.Load name

        if name = "ClaimCore.Application" then
            // F# startup types retain the source filename, rather than its declaring namespace.
            let directory = Path.Combine(root, "src", "ClaimCore.Application")

            let storageFiles =
                Directory.EnumerateFiles(directory, "*.fs", SearchOption.AllDirectories)
                |> Seq.filter (fun path ->
                    (File.ReadAllText path)
                        .StartsWith(
                            "namespace ClaimCore.Application.Storage",
                            StringComparison.Ordinal
                        ))
                |> Seq.map (
                    Path.GetFileNameWithoutExtension >> System.Text.RegularExpressions.Regex.Escape
                )
                |> String.concat "|"

            Expect.isNotEmpty storageFiles "Storage ownership must be discovered"

            let excluded =
                @"^ClaimCore\.Application\.Storage(?:\.|$)|^<StartupCode\$ClaimCore-Application>\.\$(?:"
                + storageFiles
                + @")(?:\+|$)"

            let selected = (ApiRules.subjects assembly).And().DoNotHaveFullNameMatching excluded
            ApiRules.check model.Value ArchitectureRule.Pure name selected
        else
            check ArchitectureRule.Pure assembly

    rejects ArchitectureRule.Pure typeof<ClockCall>

let private importedReference () =
    let directory = Directory.CreateTempSubdirectory("claimcore-project-edge-")

    try
        let properties = Path.Combine(directory.FullName, "edge.props")

        File.WriteAllText(
            properties,
            """<Project><ItemGroup><ProjectReference Include="ClaimCore.Application.fsproj" /></ItemGroup></Project>"""
        )

        let path = Path.Combine(directory.FullName, "client.fsproj")
        File.WriteAllText(path, "<Project><Import Project=\"edge.props\" /></Project>")
        let references = ProjectReferences.read path

        Expect.equal
            references
            (Set.singleton "ClaimCore.Application")
            "Unused imported project edge is evaluated"

        Expect.isFalse
            (ProjectReferences.permitted "ClaimCore.Wire" references)
            "Wire cannot declare the Application edge"
    finally
        directory.Delete true

let private jsonDependencies () =
    let path = Path.Combine(root, "src", "ClaimCore.Json", "ClaimCore.Json.fsproj")
    Expect.isEmpty (ProjectReferences.read path) "JSON has no product dependencies"

let private projectEdges () =
    Expect.equal
        (projectPaths.Value |> Array.map Path.GetFileNameWithoutExtension |> Set.ofArray)
        (ProjectReferences.allowed |> Map.keys |> Set.ofSeq)
        "Every product project has an edge policy"

    for path in projectPaths.Value do
        let name = Path.GetFileNameWithoutExtension path

        Expect.isTrue
            (ProjectReferences.permitted name (ProjectReferences.read path))
            (name + " evaluated project references")

    Expect.isTrue
        (ProjectReferences.permitted "ClaimCore.Wire" (Set.singleton "ClaimCore.Domain"))
        "A smaller dependency surface preserves the boundary"

    Expect.isFalse
        (ProjectReferences.permitted "Unknown.Product" Set.empty)
        "A new project requires an explicit boundary policy"

    importedReference ()

let private serializer () =
    for name in [ "ClaimCore.Json"; "ClaimCore.Wire"; "ClaimCore.RecordFormat" ] do
        check ArchitectureRule.Serializer (Assembly.Load name)

    rejects ArchitectureRule.Serializer typeof<JsonSerializerCall>

[<Tests>]
let tests =
    testList
        "architecture"
        [
            testCase
                "pure projects reject ambient effects including timezone and resources"
                ambientEffects
            testCase "JSON has no project dependencies" jsonDependencies
            testCase "evaluated product edges" projectEdges
            testCase "Json, Wire and RecordFormat avoid JsonSerializer" serializer
            testCase "closed wire vocabularies use the typed codec adapter" (fun () ->
                check ArchitectureRule.ClosedVocabularyCodecs (Assembly.Load "ClaimCore.Wire")
                rejects ArchitectureRule.ClosedVocabularyCodecs typeof<DirectEnumeration>)
        ]
