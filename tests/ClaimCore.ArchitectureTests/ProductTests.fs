module ClaimCore.ArchitectureTests.ProductTests

open System
open System.IO
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
