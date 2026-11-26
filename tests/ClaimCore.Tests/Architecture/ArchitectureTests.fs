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

let private importedReference projects =
    let directory = Directory.CreateTempSubdirectory("claimcore-project-edge-")

    try
        let properties = Path.Combine(directory.FullName, "edge.props")

        let library = projects |> Map.find "ClaimCore.Application"
        let escaped = System.Security.SecurityElement.Escape library

        File.WriteAllText(
            properties,
            $"""<Project><ItemGroup Condition="'$(Configuration)' == 'Release'"><ProjectReference Include="{escaped}" /></ItemGroup></Project>"""
        )

        let path = Path.Combine(directory.FullName, "client.fsproj")
        File.WriteAllText(path, "<Project><Import Project=\"edge.props\" /></Project>")

        Expect.isEmpty
            (ProjectReferences.read ("Debug", None) path)
            "This imported edge is absent in Debug"

        let references = ProjectReferences.read ("Release", None) path

        Expect.equal
            references
            (Set.singleton library)
            "The Release-only imported project edge is evaluated"

        Expect.isFalse
            (ProjectReferences.permitted projects "ClaimCore.Wire" references)
            "Wire cannot declare the Application edge"

        let lookalike = Path.Combine(directory.FullName, "ClaimCore.Domain.fsproj")

        Expect.isFalse
            (ProjectReferences.permitted projects "ClaimCore.Wire" (Set.singleton lookalike))
            "A different project with an allowed filename is refused"
    finally
        directory.Delete true

let private jsonDependencies () =
    let path = Path.Combine(root, "src", "ClaimCore.Json", "ClaimCore.Json.fsproj")
    Expect.isEmpty (ProjectReferences.read ("Debug", None) path) "JSON has no product dependencies"

let private projectEdges () =
    let projects =
        projectPaths.Value
        |> Array.map (fun path -> Path.GetFileNameWithoutExtension path, Path.GetFullPath path)
        |> Map.ofArray

    Expect.equal projects.Count projectPaths.Value.Length "Product project names are unique"

    Expect.equal
        (projects |> Map.keys |> Set.ofSeq)
        (ProjectReferences.allowed |> Map.keys |> Set.ofSeq)
        "Every product project has an edge policy"

    for configuration in ProjectReferences.shippingConfigurations projects["ClaimCore.Cli"] do
        for KeyValue(name, path) in projects do
            Expect.isTrue
                (ProjectReferences.permitted
                    projects
                    name
                    (ProjectReferences.read configuration path))
                $"{name} evaluated project references for {configuration}"

    Expect.isTrue
        (ProjectReferences.permitted
            projects
            "ClaimCore.Wire"
            (Set.singleton projects["ClaimCore.Domain"]))
        "A smaller dependency surface preserves the boundary"

    Expect.isFalse
        (ProjectReferences.permitted projects "Unknown.Product" Set.empty)
        "A new project requires an explicit boundary policy"

    importedReference projects

let private serializer () =
    for name in [ "ClaimCore.Json"; "ClaimCore.Wire"; "ClaimCore.RecordFormat" ] do
        check ArchitectureRule.Serializer (Assembly.Load name)

    rejects ArchitectureRule.Serializer typeof<JsonSerializerCall>

let private taskModules () =
    for assembly in products.Value do
        let selected =
            (ApiRules.subjects assembly)
                .And()
                .DoNotHaveFullNameMatching(
                    @"^ClaimCore\.Domain\.Tasks(?:\.|$)|^<StartupCode\$ClaimCore-Domain>\.\$"
                )

        ApiRules.check
            model.Value
            ArchitectureRule.TaskModules
            (assembly.GetName().Name |> nonNull)
            selected

    rejects ArchitectureRule.TaskModules typeof<TaskModuleFromClient>

[<Tests>]
let tests =
    testList
        "architecture"
        [
            testCase
                "pure projects reject ambient effects including timezone and resources"
                ambientEffects
            testCase "clients use the task registry rather than implementations" taskModules
            testCase "JSON has no project dependencies" jsonDependencies
            testCase "evaluated product edges" projectEdges
            testCase "Json, Wire and RecordFormat avoid JsonSerializer" serializer
            testCase "closed wire vocabularies use the typed codec adapter" (fun () ->
                check ArchitectureRule.ClosedVocabularyCodecs (Assembly.Load "ClaimCore.Wire")
                rejects ArchitectureRule.ClosedVocabularyCodecs typeof<DirectEnumeration>)
        ]
