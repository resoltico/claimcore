module ClaimCore.ArchitectureTests.ProductTests

open System
open System.IO
open System.Xml.Linq
open Expecto
open ArchUnitNET.Fluent
open ClaimCore.TestSupport

let private permissions = ProductPolicy.permissions
let private name = ProductPolicy.name
let private names = ProductPolicy.names
let private assemblies = ProductModel.assemblies
let private architecture = ProductModel.architecture
let private select = ProductModel.select

let private dependencyCase (part, allowed) =
    testCase (part + " respects owned component dependencies") (fun () ->
        let model = architecture.Value
        let subjects = select part
        Inspection.requireSelection model subjects |> ignore

        let failures =
            permissions
            |> List.collect (fun (other, _) ->
                if other = part || List.contains other allowed then
                    []
                else
                    let target = select other
                    Inspection.requireSelection model target |> ignore

                    Inspection.violations model (subjects.Should().NotDependOnAny(target))
                    |> List.map (fun detail -> name part + " -> " + name other + ": " + detail))

        if not failures.IsEmpty then
            failtest (String.concat Environment.NewLine failures))

let private roots () =
    let source = Path.Combine(RepositoryRoot.find (), "src")

    let actual =
        Directory.GetFiles(source, "*.fsproj", SearchOption.AllDirectories)
        |> Array.map Path.GetFileNameWithoutExtension
        |> Set.ofArray

    Expect.equal actual (Set.ofList names) "Every discovered production root must be classified"

let private noTestDependency () =
    for assembly in assemblies.Value do
        for dependency in assembly.GetReferencedAssemblies() do
            let dependencyName = nonNull dependency.Name

            Expect.isFalse
                (dependencyName.Contains("ArchUnitNET")
                 || dependencyName.Contains("Expecto")
                 || (dependencyName.StartsWith("ClaimCore.", StringComparison.Ordinal)
                     && not (List.contains dependencyName names)))
                ("Tooling leaked into " + assembly.GetName().Name + ": " + dependencyName)

let private projectGraph () =
    let source = Path.Combine(RepositoryRoot.find (), "src")
    let projects = ProjectReferences.loadProjects source
    let names = ProjectReferences.names projects

    let failures =
        projects
        |> List.collect (fun path ->
            ProjectReferences.violations names ProductPolicy.allowed path (XDocument.Load path))

    if not failures.IsEmpty then
        failtest (failures |> List.sort |> String.concat Environment.NewLine)

let private unusedForbiddenReference () =
    let source = Path.Combine(RepositoryRoot.find (), "src")
    let projects = ProjectReferences.loadProjects source
    let names = ProjectReferences.names projects
    let domain = Path.Combine(source, "ClaimCore.Domain", "ClaimCore.Domain.fsproj")

    let fixture =
        XDocument.Parse
            "<Project><ItemGroup><ProjectReference Include='../ClaimCore.Cli/ClaimCore.Cli.fsproj' /></ItemGroup></Project>"

    let failures =
        ProjectReferences.violations names (Map.ofList [ "ClaimCore.Domain", [] ]) domain fixture

    Expect.isNonEmpty failures "An unused but forbidden declared reference must fail"
    Expect.stringContains failures.Head "ClaimCore.Cli" "The failure names the forbidden target"

let private deterministic part =
    testCase (part + " avoids selected ambient clock and host IO APIs") (fun () ->
        let model = architecture.Value
        let subjects = select part

        for target in [ typeof<Console>; typeof<File>; typeof<Directory>; typeof<Environment> ] do
            let forbidden = ArchRuleDefinition.Types().That().Are(target)
            Inspection.requireSelection model forbidden |> ignore
            Inspection.check model (subjects.Should().NotDependOnAny(forbidden))

        for target, methodName in
            [
                typeof<DateTime>, "get_Now"
                typeof<DateTime>, "get_UtcNow"
                typeof<DateTime>, "get_Today"
                typeof<DateTimeOffset>, "get_Now"
                typeof<DateTimeOffset>, "get_UtcNow"
                typeof<TimeZoneInfo>, "get_Local"
            ] do
            let forbidden =
                ArchRuleDefinition
                    .MethodMembers()
                    .That()
                    .AreDeclaredIn(target)
                    .And()
                    .HaveNameContaining(methodName)

            Inspection.requireSelection model forbidden |> ignore
            Inspection.check model (subjects.Should().NotCallAny(forbidden)))

let private endpointIsolation () =
    let model = architecture.Value

    let composition =
        ArchRuleDefinition
            .Types()
            .That()
            .HaveFullNameMatching(@"^ClaimCore\.Web\.Program(?:[+/.].*)?$")

    let endpoints =
        ArchRuleDefinition.Types().That().Are(select "Web").And().AreNot(composition)

    let factories =
        ArchRuleDefinition
            .MethodMembers()
            .That()
            .AreDeclaredIn(typeof<ClaimCore.Hosting.Runtime>)
            .And()
            .HaveNameContaining("OpenPostgres")

    Inspection.requireSelection model composition |> ignore
    Inspection.requireSelection model endpoints |> ignore
    Inspection.requireSelection model factories |> ignore
    Inspection.check model (endpoints.Should().NotCallAny(factories))
    Inspection.check model (endpoints.Should().NotDependOnAny(composition))

let private cliRuntimeIsolation () =
    let model = architecture.Value

    let composition =
        ArchRuleDefinition
            .Types()
            .That()
            .HaveFullNameMatching(
                @"^(?:ClaimCore\.Cli\.RuntimeSession(?:[+/.].*)?|<StartupCode\$ClaimCore-Cli>\.\$CliRunner\+Run@.*)$"
            )

    let generatedRun =
        ArchRuleDefinition
            .Types()
            .That()
            .HaveFullNameMatching(@"^<StartupCode\$ClaimCore-Cli>\.\$CliRunner\+Run@.*$")

    let otherCli =
        ArchRuleDefinition.Types().That().Are(select "Cli").And().AreNot(composition)

    let factories =
        ArchRuleDefinition
            .MethodMembers()
            .That()
            .AreDeclaredIn(typeof<ClaimCore.Hosting.Runtime>)
            .And()
            .HaveNameContaining("OpenPostgres")

    Inspection.requireSelection model composition |> ignore
    Inspection.requireSelection model generatedRun |> ignore
    Inspection.requireSelection model otherCli |> ignore
    Inspection.requireSelection model factories |> ignore
    Inspection.check model (otherCli.Should().NotCallAny(factories))

let private persistenceDoesNotDecide () =
    let model = architecture.Value

    let storage = select "Postgres"

    let decision =
        ArchRuleDefinition.MethodMembers().That().HaveFullNameContaining("ClaimModule::decide(")

    Inspection.requireSelection model storage |> ignore
    Inspection.requireSelection model decision |> ignore
    Inspection.check model (storage.Should().NotCallAny(decision))

let private adaptersDoNotDecide () =
    let model = architecture.Value

    let decision =
        ArchRuleDefinition.MethodMembers().That().HaveFullNameContaining("ClaimModule::decide(")

    Inspection.requireSelection model decision |> ignore

    for part in [ "Cli"; "Web"; "Database" ] do
        let adapter = select part
        Inspection.requireSelection model adapter |> ignore
        Inspection.check model (adapter.Should().NotCallAny(decision))

let tests =
    testList
        "product component policy"
        ((permissions |> List.map dependencyCase)
         @ [
             testCase "all production roots are classified" roots
             testCase "declared project references obey component permissions" projectGraph
             testCase "unused forbidden declared reference is detected" unusedForbiddenReference
             testCase "production assemblies do not depend on test tooling" noTestDependency
             testCase "endpoint helpers cannot reach runtime composition" endpointIsolation
             testCase "CLI helpers cannot open the PostgreSQL runtime" cliRuntimeIsolation
             testCase
                 "persistence cannot call the domain decision directly"
                 persistenceDoesNotDecide
             testCase "adapters cannot call the domain decision directly" adaptersDoNotDecide
         ]
         @ ([ "Domain"; "RecordFormat"; "Contracts" ] |> List.map deterministic))
