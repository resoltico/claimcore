module ClaimCore.ArchitectureTests.ProductTests

open System
open System.IO
open System.Xml.Linq
open Expecto
open ArchUnitNET.Fluent
open ClaimCore.TestSupport

/// The permitted dependencies of the native service and its local presentation adapters.
let private permissions =
    [
        "Domain", []
        "RecordFormat", [ "Domain" ]
        "Application", [ "Domain"; "RecordFormat" ]
        "Contracts", [ "Domain"; "RecordFormat"; "Application" ]
        "HostSecurity", []
        "Postgres", [ "Domain"; "RecordFormat"; "Application" ]
        "Cli",
        [
            "Domain"
            "RecordFormat"
            "Application"
            "Contracts"
            "Postgres"
            "HostSecurity"
        ]
        "Web",
        [
            "Domain"
            "RecordFormat"
            "Application"
            "Contracts"
            "Postgres"
            "HostSecurity"
        ]
        "Database", [ "Application"; "Domain"; "Postgres"; "HostSecurity"; "RecordFormat" ]
    ]

let private name part = "ClaimCore." + part
let private names = permissions |> List.map (fst >> name)

let private assemblies =
    lazy (Inspection.loadRequired AppContext.BaseDirectory names)

let private architecture = lazy (Inspection.build assemblies.Value)

let private select part =
    let assembly =
        assemblies.Value |> Array.find (fun item -> item.GetName().Name = name part)

    ArchRuleDefinition.Types().That().ResideInAssembly(assembly)

let private dependencyCase (part, allowed) =
    testCase (part + " respects owned component dependencies") (fun () ->
        let model = architecture.Value
        let subjects = select part
        Inspection.requireSelection model subjects |> ignore

        for other, _ in permissions do
            if other <> part && not (List.contains other allowed) then
                let target = select other
                Inspection.requireSelection model target |> ignore
                Inspection.check model (subjects.Should().NotDependOnAny(target)))

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

    let allowed =
        permissions
        |> List.map (fun (part, targets) -> name part, List.map name targets)
        |> Map.ofList

    projects
    |> List.collect (fun path ->
        ProjectReferences.violations names allowed path (XDocument.Load path))
    |> fun failures ->
        Expect.isEmpty
            failures
            "Every declared product ProjectReference must follow the component policy"

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

let private persistenceDoesNotDecide () =
    let model = architecture.Value

    let storage =
        ArchRuleDefinition
            .Types()
            .That()
            .Are(select "Postgres")
            .And()
            .DoNotResideInNamespaceMatching(@"^ClaimCore\.Hosting(?:\..*)?$")

    let decision =
        ArchRuleDefinition.MethodMembers().That().HaveFullNameContaining("ClaimModule::decide(")

    Inspection.requireSelection model storage |> ignore
    Inspection.requireSelection model decision |> ignore
    Inspection.check model (storage.Should().NotCallAny(decision))

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
             testCase
                 "persistence cannot call the domain decision directly"
                 persistenceDoesNotDecide
         ]
         @ ([ "Domain"; "RecordFormat"; "Contracts" ] |> List.map deterministic))
