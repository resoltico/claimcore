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
        check ArchitectureRule.Pure (Assembly.Load name)

    rejects ArchitectureRule.Pure typeof<ClockCall>

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
            (ProjectReferences.permitted "ClaimCore.Wire" references)
            "Only Web and Database may declare this edge"
    finally
        directory.Delete true

let private jsonDependencies () =
    let path = Path.Combine(root, "src", "ClaimCore.Json", "ClaimCore.Json.fsproj")
    Expect.isEmpty (ProjectReferences.read path) "JSON has no product dependencies"

let private postgres () =
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

    for assembly in products.Value do
        let name = assembly.GetName().Name |> nonNull

        if not (List.contains name [ "ClaimCore.Postgres"; "claimcore-server" ]) then
            check ArchitectureRule.Postgres assembly

    rejects ArchitectureRule.Postgres typeof<PostgresReference>

let private serializer () =
    for name in [ "ClaimCore.Json"; "ClaimCore.Wire"; "ClaimCore.RecordFormat" ] do
        check ArchitectureRule.Serializer (Assembly.Load name)

    rejects ArchitectureRule.Serializer typeof<JsonSerializerCall>

let private publishedClosure () =
    let directory = Directory.CreateTempSubdirectory("claimcore-client-closure-")

    try
        let dependencies = Path.Combine(directory.FullName, "claimcore.deps.json")
        File.WriteAllText(dependencies, "{\"libraries\":{}}")

        let check () =
            ClaimCore.TestSupport.PublishedClientClosure.violations directory.FullName

        Expect.isEmpty (check ()) "A clean synthetic deliverable is admitted"
        let source = typeof<ClaimCore.Application.IBusinessCalendar>.Assembly.Location
        let target = Path.Combine(directory.FullName, Path.GetFileName source)
        File.Copy(source, target)

        Expect.equal
            (check ())
            [ "ClaimCore.Application" ]
            "A real compiled server assembly is rejected"


    finally
        directory.Delete true

let private nativePublication () =
    let rid = RuntimeInformation.RuntimeIdentifier

    let directory =
        Path.Combine(
            root,
            "artifacts",
            "verification",
            "native-cli-" + Guid.NewGuid().ToString("N")
        )

    Directory.CreateDirectory(
        directory,
        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
    )
    |> ignore

    let mutable passed = false

    let run executable arguments =
        let start =
            ProcessStartInfo(
                executable,
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            )

        CliProcessEnvironment.clearInherited start
        CliProcessEnvironment.isolateProfile start directory

        for argument in arguments do
            start.ArgumentList.Add argument

        let result =
            try
                BoundedProcess.run start None 1_048_576 120000
            with :? BoundedProcessFailure as failure ->
                File.WriteAllBytes(Path.Combine(directory, "last.stdout"), failure.StandardOutput)
                File.WriteAllBytes(Path.Combine(directory, "last.stderr"), failure.StandardError)
                reraise ()

        File.WriteAllBytes(Path.Combine(directory, "last.stdout"), result.StandardOutput)
        File.WriteAllBytes(Path.Combine(directory, "last.stderr"), result.StandardError)

        Expect.equal
            result.ExitCode
            0
            ("Native CLI process succeeds: " + Encoding.UTF8.GetString result.StandardError)

        result.StandardOutput

    try
        let configuration =
            typeof<ClaimCore.Cli.RemoteConfiguration>
                .Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()
            |> nonNull
            |> _.Configuration

        let output = Path.Combine(directory, "publish")

        run
            "dotnet"
            [
                "publish"
                Path.Combine(root, "src", "ClaimCore.Cli", "ClaimCore.Cli.fsproj")
                "--configuration"
                configuration
                "--no-build"
                "--no-restore"
                "--output"
                output
            ]
        |> ignore

        Expect.isEmpty
            (PublishedClientClosure.violations output)
            "The actual publication contains no server dependencies"

        ThirdPartyNotices.qualify root output "ClaimCore.Cli" |> ignore
        let executable = Path.Combine(output, "claimcore")

        use version =
            JsonDocument.Parse((run executable [ "version"; "--json" ]).AsMemory())

        Expect.equal
            (version.RootElement.GetProperty("product").GetString())
            "ClaimCore"
            "The native apphost executes real version discovery"

        let help = run executable [ "help"; "case"; "open" ] |> Encoding.UTF8.GetString

        Expect.stringContains
            help
            "claimcore case open"
            "Offline command help executes from the publication"

        let schema =
            run executable [ "schema"; "operation.execute"; "request" ]
            |> Encoding.UTF8.GetString

        use parsedSchema = JsonDocument.Parse schema

        Expect.equal
            parsedSchema.RootElement.ValueKind
            JsonValueKind.Object
            "The published schema is JSON"

        Expect.stringContains
            schema
            "\"operationId\""
            "The mutation schema retains its identity contract"

        printfn
            "Native CLI publication: RID=%s; configuration=%s; apphost/help/schema/notices/source/closure passed."
            rid
            configuration

        passed <- true
    finally
        if passed then
            Directory.Delete(directory, true)
        else
            Console.Error.WriteLine("Native CLI publication evidence retained: " + directory)

let private serviceModelWithoutProtocol () =
    check ArchitectureRule.ServiceModelWithoutProtocol (Assembly.Load "ClaimCore.Application")
    check ArchitectureRule.ServiceModelWithoutProtocol (Assembly.Load "ClaimCore.Postgres")
    rejects ArchitectureRule.ServiceModelWithoutProtocol typeof<ProtocolFromDomain>

let private domainBoundary () =
    check ArchitectureRule.DomainBoundary (Assembly.Load "ClaimCore.Domain")
    rejects ArchitectureRule.DomainBoundary typeof<RecordFormatFromDomain>

[<Tests>]
let tests =
    testList
        "architecture"
        [
            testCase
                "pure projects reject ambient effects including timezone and resources"
                ambientEffects
            testCase "JSON has no project dependencies" jsonDependencies
            testCase "Domain excludes outer layers" domainBoundary
            testCase "hosts use only the service surface and ports" (fun () ->
                check ArchitectureRule.ServiceSurface (Assembly.Load "claimcore-server")
                rejects ArchitectureRule.ServiceSurface typeof<ApplicationInternalsFromWeb>)
            testCase "Domain is independent of JSON" (fun () ->
                check ArchitectureRule.DomainJsonFree (Assembly.Load "ClaimCore.Domain")
                rejects ArchitectureRule.DomainJsonFree typeof<JsonFromDomain>)
            testCase
                "Application and Postgres use the service model without Protocol"
                serviceModelWithoutProtocol
            testCase "evaluated product edges and Postgres boundary" postgres
            testCase
                "published client closure rejects compiled server dependencies"
                publishedClosure
            testCase
                ("published CLI executes and qualifies its deliverable ("
                 + RuntimeInformation.RuntimeIdentifier
                 + ")")
                nativePublication
            testCase "JSON, Contract and RecordFormat avoid JsonSerializer" serializer
            testCase "closed wire vocabularies use the typed codec adapter" (fun () ->
                check ArchitectureRule.ClosedVocabularyCodecs (Assembly.Load "ClaimCore.Wire")
                rejects ArchitectureRule.ClosedVocabularyCodecs typeof<DirectEnumeration>)
        ]
