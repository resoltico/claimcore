module ClaimCore.Tests.Architecture.ArchitectureTests

open System
open System.IO
open System.Reflection
open Expecto
open ArchUnitNET.Loader
open type ArchUnitNET.Fluent.ArchRuleDefinition
open ClaimCore.Tests.Architecture.NegativeFixtures

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
                 |> Option.defaultValue (Path.GetFileNameWithoutExtension path |> nonNull)

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
        (Types().That().ResideInAssembly assembly)

let private rejects rule (fixture: System.Type) =
    let subjects = Types().That().HaveFullName(fixture.FullName |> nonNull)

    Expect.isFalse
        (subjects.GetObjects model.Value |> Seq.isEmpty)
        "The compiled negative fixture must be present"

    Expect.isFalse
        ((ApiRules.rule rule subjects).HasNoViolations model.Value)
        "The rule detects a compiled violation, rather than only throwing"

    Expect.throwsT<InvalidOperationException>
        (fun () -> ApiRules.check model.Value rule fixture.Name subjects)
        "The compiled prohibited dependency must be detected"

let private ambientEffects () =
    for name in
        [
            "ClaimCore.Json"
            "ClaimCore.Contract"
            "ClaimCore.Domain"
            "ClaimCore.RecordFormat"
            "ClaimCore.Application"
        ] do
        check "pure" (Assembly.Load name)

    for fixture in
        [
            typeof<ClockCall>
            typeof<FileCall>
            typeof<FileReaderCall>
            typeof<EnvironmentCall>
            typeof<RandomCall>
            typeof<NetworkCall>
            typeof<TimeZoneCall>
            typeof<ResourceCall>
        ] do
        rejects "pure" fixture

let private importedReference () =
    let directory = Directory.CreateTempSubdirectory("claimcore-project-edge-")

    try
        let properties = Path.Combine(directory.FullName, "edge.props")

        File.WriteAllText(
            properties,
            """<Project><ItemGroup><ProjectReference Include="ClaimCore.Postgres.fsproj" /></ItemGroup></Project>"""
        )

        let path = Path.Combine(directory.FullName, "client.fsproj")
        File.WriteAllText(path, "<Project><Import Project=\"edge.props\" /></Project>")
        let references = ProjectReferences.read path

        Expect.equal
            references
            (Set.singleton "ClaimCore.Postgres")
            "Unused imported project edge is evaluated"

        Expect.isFalse
            (ProjectReferences.permitted "ClaimCore.Contract" references)
            "Only Web and Database may declare this edge"
    finally
        directory.Delete true

let private jsonDependencies () =
    let path = Path.Combine(root, "src", "ClaimCore.Json", "ClaimCore.Json.fsproj")
    Expect.isEmpty (ProjectReferences.read path) "JSON has no product dependencies"

let private postgres () =
    for path in projectPaths.Value do
        let name = Path.GetFileNameWithoutExtension path |> nonNull

        Expect.isTrue
            (ProjectReferences.permitted name (ProjectReferences.read path))
            (name + " evaluated Postgres reference")

    importedReference ()

    for assembly in products.Value do
        let name = assembly.GetName().Name |> nonNull

        if
            not (
                List.contains
                    name
                    [ "ClaimCore.Postgres"; "claimcore-server"; "claimcore-database" ]
            )
        then
            check "postgres" assembly

    rejects "postgres" typeof<PostgresReference>

let private serializer () =
    for name in [ "ClaimCore.Json"; "ClaimCore.Contract"; "ClaimCore.RecordFormat" ] do
        check "serializer" (Assembly.Load name)

    rejects "serializer" typeof<JsonSerializerCall>
    // Direct writers are permitted by the adopted policy; JsonSerializer is not.
    ApiRules.check
        model.Value
        "serializer"
        "writer control"
        (Types().That().HaveFullName(typeof<JsonWriterCall>.FullName |> nonNull))

let private durableCodecs () =
    check "durable" (Assembly.Load "ClaimCore.RecordFormat")
    rejects "durable" typeof<WireCodecCall>

let private publishedClosure () =
    let directory = Directory.CreateTempSubdirectory("claimcore-client-closure-")

    try
        let dependencies = Path.Combine(directory.FullName, "claimcore.deps.json")
        File.WriteAllText(dependencies, "{\"libraries\":{}}")

        let check () =
            ClaimCore.TestSupport.PublishedClientClosure.violations directory.FullName

        Expect.isEmpty (check ()) "A clean synthetic deliverable is admitted"
        let source = typeof<ClaimCore.Domain.Claim>.Assembly.Location
        let target = Path.Combine(directory.FullName, Path.GetFileName source |> nonNull)
        File.Copy(source, target)
        Expect.equal (check ()) [ "ClaimCore.Domain" ] "A real compiled server assembly is rejected"
        File.Delete target
        File.WriteAllText(dependencies, "{\"libraries\":{\"ClaimCore.Application/0.11.0\":{}}}")

        Expect.equal
            (check ())
            [ "ClaimCore.Application" ]
            "Resolved transitive server dependencies are rejected"
    finally
        directory.Delete true

[<Tests>]
let tests =
    testList
        "architecture"
        [
            testCase
                "pure projects reject ambient effects including timezone and resources"
                ambientEffects
            testCase "durable codecs reject wire codec dependencies" durableCodecs
            testCase "JSON has no project dependencies" jsonDependencies
            testCase "only Web and Database may reference Postgres" postgres
            testCase
                "published client closure rejects compiled server dependencies"
                publishedClosure
            testCase "JSON, Contract and RecordFormat avoid JsonSerializer" serializer
        ]
