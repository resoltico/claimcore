module ClaimCore.ArchitectureTests.ProductTests

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Xml.Linq
open Expecto
open ArchUnitNET.Fluent
open ClaimCore.TestSupport

let private prefix = "ClaimCore."

let private part (componentName: string) = componentName.Substring(prefix.Length)

/// The reviewed product graph, expressed in the historical short part labels the selectors use.
let private permissions =
    ProductPolicy.product
    |> List.map (fun item -> part item.Name, item.DependsOn |> List.map part)

let private name = ProductPolicy.name
let private assemblies = ProductModel.assemblies
let private architecture = ProductModel.architecture
let private select = ProductModel.select

let private dependencyCase (subject, allowed) =
    testCase (subject + " respects owned component dependencies") (fun () ->
        let model = architecture.Value
        let subjects = select subject
        Inspection.requireSelection model subjects |> ignore

        let failures =
            permissions
            |> List.collect (fun (other, _) ->
                if other = subject || List.contains other allowed then
                    []
                else
                    let target = select other
                    Inspection.requireSelection model target |> ignore

                    Inspection.violations model (subjects.Should().NotDependOnAny(target))
                    |> List.map (fun detail -> name subject + " -> " + name other + ": " + detail))

        if not failures.IsEmpty then
            failtest (String.concat Environment.NewLine failures))

let private noTestDependency () =
    let productNames = ProductPolicy.names

    for assembly in assemblies.Value do
        for dependency in assembly.GetReferencedAssemblies() do
            let dependencyName = nonNull dependency.Name

            Expect.isFalse
                (dependencyName.Contains("ArchUnitNET")
                 || dependencyName.Contains("Expecto")
                 || (dependencyName.StartsWith(prefix, StringComparison.Ordinal)
                     && not (List.contains dependencyName productNames)))
                ("Tooling leaked into " + assembly.GetName().Name + ": " + dependencyName)

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

let private forbiddenTypes =
    [
        typeof<Console>
        typeof<File>
        typeof<Directory>
        typeof<Environment>
        typeof<Random>
        typeof<Thread>
    ]

/// Ambient identity and calendar reads that would bypass the caller-supplied operation ID or the
/// installation's stored business zone.
let private forbiddenCalls =
    [
        typeof<DateTime>, "get_Now"
        typeof<DateTime>, "get_UtcNow"
        typeof<DateTime>, "get_Today"
        typeof<DateTimeOffset>, "get_Now"
        typeof<DateTimeOffset>, "get_UtcNow"
        typeof<TimeZoneInfo>, "get_Local"
        typeof<Guid>, "NewGuid"
    ]

let private deterministic subject =
    testCase (subject + " avoids selected ambient clock, identity and host IO APIs") (fun () ->
        let model = architecture.Value
        let subjects = select subject

        for target in forbiddenTypes do
            let forbidden = ArchRuleDefinition.Types().That().Are(target)
            Inspection.requireSelection model forbidden |> ignore
            Inspection.check model (subjects.Should().NotDependOnAny(forbidden))

        for target, methodName in forbiddenCalls do
            let forbidden =
                ArchRuleDefinition
                    .MethodMembers()
                    .That()
                    .AreDeclaredIn(target)
                    .And()
                    .HaveNameContaining(methodName)

            Inspection.requireSelection model forbidden |> ignore
            Inspection.check model (subjects.Should().NotCallAny(forbidden)))

/// The Web host is one assembly, so its composition root is confined by a compiled rule rather
/// than by a project edge. The selector is anchored to the `Program` module the author declared,
/// and the positive assertion keeps the rule from passing merely because composition moved out of
/// the selector's reach.
/// Contracts owns every CLI-v3 and Web-v2 codec, including the database-free discovery payloads.
/// A renderer that authored wire JSON itself would fork the generated contract without its schema,
/// corpus, or fingerprint noticing, so no wire-contract renderer may reach a JSON writer at all.
/// `Database` is deliberately outside this set: it publishes no wire contract and references no
/// contract projection, so its administrative output is not a protocol surface.
let private wireRenderersDoNotAuthorJson () =
    let model = architecture.Value
    let writer = ArchRuleDefinition.Types().That().Are(typeof<Utf8JsonWriter>)
    let owner = select "Contracts"

    Inspection.requireSelection model writer |> ignore
    Inspection.requireSelection model owner |> ignore

    Expect.isNonEmpty
        (Inspection.violations model (owner.Should().NotDependOnAny(writer)))
        "Contracts must be the component that authors wire JSON"

    for subject in [ "Cli"; "CliProtocol"; "Web" ] do
        let renderer = select subject
        Inspection.requireSelection model renderer |> ignore
        Inspection.check model (renderer.Should().NotDependOnAny(writer))

/// The installation calendar is a stored IANA zone plus one captured instant. Only the components
/// that own it may touch the host time-zone database at all: Postgres validates and stores the
/// zone, Hosting resolves it once and derives the business date. Any other component resolving or
/// converting a zone would be deriving a second calendar from the host rather than from the
/// installation, which is the hazard the stored zone exists to remove.
let private installationOwnsTheCalendar () =
    let model = architecture.Value
    let zones = ArchRuleDefinition.Types().That().Are(typeof<TimeZoneInfo>)

    Inspection.requireSelection model zones |> ignore

    for owner in [ "Hosting"; "Postgres" ] do
        let component' = select owner
        Inspection.requireSelection model component' |> ignore

        Expect.isNonEmpty
            (Inspection.violations model (component'.Should().NotDependOnAny(zones)))
            ("The installation calendar owner must resolve zones: " + owner)

    for subject in
        [
            "Domain"
            "RecordFormat"
            "Application"
            "Contracts"
            "Cli"
            "CliProtocol"
            "Web"
            "Database"
        ] do
        let other = select subject
        Inspection.requireSelection model other |> ignore
        Inspection.check model (other.Should().NotDependOnAny(zones))

let private endpointIsolation () =
    let model = architecture.Value

    let composition =
        ArchRuleDefinition
            .Types()
            .That()
            .HaveFullNameMatching(@"^ClaimCore\.Web\.Program(?:[+/.].*)?$")

    let endpoints =
        ArchRuleDefinition.Types().That().Are(select "Web").And().AreNot(composition)

    let runtime =
        ArchRuleDefinition.Types().That().Are(typeof<ClaimCore.Hosting.Runtime>)

    Inspection.requireSelection model composition |> ignore
    Inspection.requireSelection model endpoints |> ignore
    Inspection.requireSelection model runtime |> ignore

    Expect.isNonEmpty
        (Inspection.violations model (composition.Should().NotDependOnAny(runtime)))
        "The Web composition root must be the component that opens the runtime"

    Inspection.check model (endpoints.Should().NotDependOnAny(runtime))
    Inspection.check model (endpoints.Should().NotDependOnAny(composition))

let tests =
    testList
        "product component policy"
        ((permissions |> List.map dependencyCase)
         @ [
             testCase "unused forbidden declared reference is detected" unusedForbiddenReference
             testCase "production assemblies do not depend on test tooling" noTestDependency
             testCase "endpoint helpers cannot reach runtime composition" endpointIsolation
         ])
