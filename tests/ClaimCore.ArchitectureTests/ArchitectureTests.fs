module ClaimCore.ArchitectureTests.ArchitectureTests

open ClaimCore.Contract

open System
open System.Reflection
open Expecto
open ArchUnitNET.Loader
open type ArchUnitNET.Fluent.ArchRuleDefinition
open ClaimCore.ArchitectureTests.NegativeFixtures

let private root = ClaimCore.TestSupport.RepositoryRoot.find ()
let private contract = lazy (Manifest.read root)
let private projects = lazy (Manifest.projects root)

let private assemblies =
    lazy (contract.Value.Projects.Keys |> Seq.map Assembly.Load |> Seq.toArray)

let private model =
    lazy
        (ArchLoader()
            .LoadAssemblies(
                Array.append
                    assemblies.Value
                    [|
                        typeof<ClockCall>.Assembly
                        typeof<Guid>.Assembly
                        typeof<System.Net.Http.HttpClient>.Assembly
                        typeof<System.Text.Json.JsonSerializer>.Assembly
                    |]
            )
            .Build())

let private resolvedGraph =
    lazy
        (projects.Value
         |> Map.map (fun _ path ->
             Manifest.references path |> ignore
             Manifest.evaluatedReferences path))

let private actualGraph () = resolvedGraph.Value

let private reject action fragment =
    let error =
        try
            action ()
            None
        with :? InvalidOperationException as error ->
            Some error.Message

    Expect.isSome error "The negative control must be rejected"
    Expect.stringContains (Option.get error) fragment "The refusal identifies the project and rule"

let private graphTests =
    [
        testCase "imported project edges are evaluated and refused" (fun () ->
            let directory = System.IO.Directory.CreateTempSubdirectory("claimcore-edges-")

            try
                let file name =
                    System.IO.Path.Combine(directory.FullName, name)

                System.IO.File.WriteAllText(
                    file "forbidden.props",
                    "<Project><ItemGroup><ProjectReference Include=\"ClaimCore.Application.fsproj\" /></ItemGroup></Project>"
                )

                System.IO.File.WriteAllText(file "ClaimCore.Application.fsproj", "<Project />")

                System.IO.File.WriteAllText(
                    file "client.fsproj",
                    "<Project><Import Project=\"forbidden.props\" /></Project>"
                )

                let edges = Manifest.evaluatedReferences (file "client.fsproj")

                Expect.equal
                    edges
                    (Set.singleton "ClaimCore.Application")
                    "Imported edge is visible"

                let changed = actualGraph () |> Map.add "ClaimCore.Cli" edges

                reject
                    (fun () -> ProjectGraph.check contract.Value changed)
                    "ClaimCore.Cli projectGraph"
            finally
                directory.Delete true)
        testCase "client protocol facade signatures hide semantic types" (fun () ->
            typeof<ClaimCore.Hosting.IServiceRuntime>.Assembly.GetExportedTypes()
            |> Array.toList
            |> FacadeSignatures.check

            reject
                (fun () -> FacadeSignatures.check [ typeof<SemanticFacade> ])
                "Semantic facade type")
        testCase "compiler reference paths match the permitted product graph" (fun () ->
            let product = contract.Value.Projects.Keys |> Set.ofSeq

            for KeyValue(name, allowed) in contract.Value.Projects do
                let resolved =
                    Manifest.resolvedReferences projects.Value[name] |> Set.intersect product

                let forbidden = Set.difference resolved allowed
                Expect.isEmpty forbidden (name + " resolved compiler references"))
        testCase "imported assembly references reach compiler resolution" (fun () ->
            let directory =
                System.IO.Directory.CreateTempSubdirectory("claimcore-assembly-edges-")

            try
                let file name =
                    System.IO.Path.Combine(directory.FullName, name)

                let assembly = typeof<ClaimCore.Application.PrincipalKey>.Assembly.Location

                System.IO.File.WriteAllText(
                    file "forbidden.props",
                    "<Project><ItemGroup><Reference Include=\"ClaimCore.Application\"><HintPath>"
                    + assembly
                    + "</HintPath></Reference></ItemGroup></Project>"
                )

                System.IO.File.WriteAllText(
                    file "client.fsproj",
                    "<Project><Import Project=\"forbidden.props\" /><Target Name=\"ResolveReferences\"><ItemGroup><ReferencePath Include=\"@(Reference->'%(HintPath)')\" /></ItemGroup></Target></Project>"
                )

                let resolved = Manifest.resolvedReferences (file "client.fsproj")

                Expect.equal
                    resolved
                    (Set.singleton "ClaimCore.Application")
                    "Imported compiler edge visible"

                let forbidden = Set.difference resolved contract.Value.Projects["ClaimCore.Cli"]
                Expect.equal forbidden resolved "Client restriction rejects imported assembly"
            finally
                directory.Delete true)
        testCase "compiled CLI has no semantic assembly dependency" (fun () ->
            let references = Assembly.Load("ClaimCore.Cli").GetReferencedAssemblies()

            let forbidden =
                set
                    [
                        "ClaimCore.Domain"
                        "ClaimCore.Application"
                        "ClaimCore.RecordFormat"
                        "ClaimCore.Contracts"
                        "ClaimCore.Hosting"
                        "ClaimCore.Postgres"
                    ]

            for item in references do
                Expect.isFalse
                    (Set.contains (item.Name |> nonNull) forbidden)
                    "Client assembly closure edge")
        testCase
            "[CC-ARCH-001] declared project references equal the manifest in both directions"
            (fun () -> ProjectGraph.check contract.Value (actualGraph ()))
        testCase "a missing required edge identifies its project" (fun () ->
            let changed = actualGraph () |> Map.add "ClaimCore.Application" Set.empty

            reject
                (fun () -> ProjectGraph.check contract.Value changed)
                "ClaimCore.Application projectGraph")
        testCase "Contract rejects dependencies on other product projects" (fun () ->
            for name in contract.Value.Projects.Keys do
                if name <> "ClaimCore.Contract" then
                    let changed =
                        actualGraph ()
                        |> Map.add "ClaimCore.Contract" (Set.singleton name)

                    reject
                        (fun () -> ProjectGraph.check contract.Value changed)
                        "ClaimCore.Contract projectGraph")
        testCase "an undeclared edge identifies its project" (fun () ->
            let changed =
                actualGraph ()
                |> Map.add "ClaimCore.Domain" (Set.singleton "ClaimCore.Postgres")

            reject
                (fun () -> ProjectGraph.check contract.Value changed)
                "ClaimCore.Domain projectGraph")
        testCase "internals grants name a permitted referencer or a test project" (fun () ->
            let tests =
                projects.Value.Keys
                |> Seq.filter (fun name -> name.EndsWith("Tests", StringComparison.Ordinal))
                |> Set.ofSeq

            for assembly in assemblies.Value do
                ProjectGraph.internals contract.Value tests assembly)
        testCase "an unpermitted internals recipient identifies the grantor" (fun () ->
            reject
                (fun () ->
                    ProjectGraph.grant
                        contract.Value
                        Set.empty
                        "ClaimCore.Domain"
                        "ClaimCore.Database")
                "ClaimCore.Domain internalsVisibleTo")
        testCase "an empty compiled subject selection is rejected" (fun () ->
            reject
                (fun () ->
                    ApiRules.check
                        model.Value
                        "onlyTheseMayUseTimeZoneInfo"
                        "ClaimCore.Domain"
                        (Types().That().HaveFullName("AbsentArchitectureSubject")))
                "ClaimCore.Domain onlyTheseMayUseTimeZoneInfo")
        testCase "the named rules cannot silently disappear" (fun () ->
            Expect.equal
                (contract.Value.Rules.Keys |> Set.ofSeq)
                ApiRules.names
                "Exact architecture rules")
    ]

let private positiveRules =
    ApiRules.names
    |> Set.toList
    |> List.map (fun name ->
        testCase ("compiled " + name) (fun () ->
            let selected = contract.Value.Rules[name]

            for project in contract.Value.Projects.Keys do
                let applies =
                    if
                        name = "freeOfClockFileEnvironmentRandomnessAndNetwork"
                        || name = "recordFormatAvoidsFrameworkSerialization"
                        || name = "clientAvoidsServerSemantics"
                        || name = "httpHostAvoidsDirectSemantics"
                    then
                        Set.contains project selected
                    elif
                        name = "onlyTheseMayReferencePostgres" && project = "ClaimCore.Postgres"
                    then
                        false
                    elif
                        name = "onlyTheseMayReferenceHosting" && project = "ClaimCore.Hosting"
                    then
                        false
                    else
                        not (Set.contains project selected)

                if applies then
                    ApiRules.check
                        model.Value
                        name
                        project
                        (Types().That().ResideInAssembly(Assembly.Load project))))

let private negativeRules =
    [
        "clientAvoidsServerSemantics", typeof<SemanticFacade>
        "httpHostAvoidsDirectSemantics", typeof<SemanticFacade>
        "onlyTheseMayReferencePostgres", typeof<PostgresReference>
        "onlyTheseMayReferenceHosting", typeof<HostingReference>
        "onlyTheseMayUseTimeZoneInfo", typeof<TimeZoneCall>
        "recordFormatAvoidsFrameworkSerialization", typeof<JsonSerializerCall>
        "recordFormatAvoidsFrameworkSerialization", typeof<JsonWriterCall>
        "freeOfClockFileEnvironmentRandomnessAndNetwork", typeof<ClockCall>
        "freeOfClockFileEnvironmentRandomnessAndNetwork", typeof<FileCall>
        "freeOfClockFileEnvironmentRandomnessAndNetwork", typeof<EnvironmentCall>
        "freeOfClockFileEnvironmentRandomnessAndNetwork", typeof<RandomCall>
        "freeOfClockFileEnvironmentRandomnessAndNetwork", typeof<NetworkCall>
    ]
    |> List.map (fun (name, fixture) ->
        testCase ("compiled negative " + name + " " + fixture.Name) (fun () ->
            let subjects = Types().That().HaveFullName(fixture.FullName |> nonNull)

            Expect.isFalse
                (subjects.GetObjects model.Value |> Seq.isEmpty)
                "Compiled negative fixture is present"

            reject
                (fun () -> ApiRules.check model.Value name "ClaimCore.Domain" subjects)
                ("ClaimCore.Domain " + name)))

[<Tests>]
let tests = testList "architecture" (graphTests @ positiveRules @ negativeRules)
