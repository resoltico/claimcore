module ClaimCore.Tests.Architecture.ArchitectureTests

open ClaimCore.Domain

open System
open System.IO
open System.Reflection
open Expecto
open ArchUnitNET.Loader
open type ArchUnitNET.Fluent.ArchRuleDefinition
open ClaimCore.Tests.Architecture.NegativeFixtures
open ClaimCore.Tests.Architecture.ApiRules
open System.Runtime.CompilerServices
open System.Text.RegularExpressions

[<NoEquality; NoComparison>]
type private DomainTargetScenario =
    {
        Assembly: string
        Direct: System.Type
        DirectMember: string
        Task: System.Type
        TaskMember: string
    }

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
            "ClaimCore.Protocol"
            "ClaimCore.Domain"
            "ClaimCore.RecordFormat"
            "ClaimCore.Application"
        ] do
        check ArchitectureRule.Pure (Assembly.Load name)

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
        rejects ArchitectureRule.Pure fixture

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
            (ProjectReferences.permitted "ClaimCore.Protocol" references)
            "Only Web and Database may declare this edge"
    finally
        directory.Delete true

let private jsonDependencies () =
    let path = Path.Combine(root, "src", "ClaimCore.Json", "ClaimCore.Json.fsproj")
    Expect.isEmpty (ProjectReferences.read path) "JSON has no product dependencies"

let private postgres () =
    Expect.equal
        (projectPaths.Value
         |> Array.map (Path.GetFileNameWithoutExtension >> nonNull)
         |> Set.ofArray)
        (ProjectReferences.allowed |> Map.keys |> Set.ofSeq)
        "Every product project has an edge policy"

    for path in projectPaths.Value do
        let name = Path.GetFileNameWithoutExtension path |> nonNull

        Expect.isTrue
            (ProjectReferences.permitted name (ProjectReferences.read path))
            (name + " evaluated project references")

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
            check ArchitectureRule.Postgres assembly

    rejects ArchitectureRule.Postgres typeof<PostgresReference>

let private serializer () =
    for name in [ "ClaimCore.Json"; "ClaimCore.Protocol"; "ClaimCore.RecordFormat" ] do
        check ArchitectureRule.Serializer (Assembly.Load name)

    rejects ArchitectureRule.Serializer typeof<JsonSerializerCall>
    // Direct writers are permitted by the adopted policy; JsonSerializer is not.
    ApiRules.check
        model.Value
        ArchitectureRule.Serializer
        "writer control"
        (ApiRules.subjects typeof<JsonWriterCall>.Assembly
         |> fun selected -> selected.And().HaveFullName(typeof<JsonWriterCall>.FullName |> nonNull))

let private durableCodecs () =
    check ArchitectureRule.DurableNames (Assembly.Load "ClaimCore.RecordFormat")
    rejects ArchitectureRule.DurableNames typeof<WireCodecCall>

let private publishedClosure () =
    let directory = Directory.CreateTempSubdirectory("claimcore-client-closure-")

    try
        let dependencies = Path.Combine(directory.FullName, "claimcore.deps.json")
        File.WriteAllText(dependencies, "{\"libraries\":{}}")

        let check () =
            ClaimCore.TestSupport.PublishedClientClosure.violations directory.FullName

        Expect.isEmpty (check ()) "A clean synthetic deliverable is admitted"
        let source = typeof<ClaimCore.Application.IBusinessCalendar>.Assembly.Location
        let target = Path.Combine(directory.FullName, Path.GetFileName source |> nonNull)
        File.Copy(source, target)

        Expect.equal
            (check ())
            [ "ClaimCore.Application" ]
            "A real compiled server assembly is rejected"

        File.Delete target
        File.WriteAllText(dependencies, "{\"libraries\":{\"ClaimCore.Application/0.11.0\":{}}}")

        Expect.equal
            (check ())
            [ "ClaimCore.Application" ]
            "Resolved transitive server dependencies are rejected"
    finally
        directory.Delete true

// This selector retains the assembly boundary while including F# StartupCode bodies.
let private shapeSubjects (fixture: System.Type) memberName =
    ApiRules.subjects fixture.Assembly
    |> fun selected ->
        selected
            .And()
            .HaveFullNameMatching(
                "^"
                + Regex.Escape(nonNull fixture.FullName)
                + "$|\\+"
                + Regex.Escape memberName
                + "@\\d+"
            )

let private shapeControls () =
    let cases =
        [
            ArchitectureRule.ServiceSurface,
            typeof<ApplicationInternalsFromWeb>,
            [ "InternalsDirect" ]
            ArchitectureRule.ServiceSurface,
            typeof<ApplicationInternalsFromWebTask>,
            [ "InternalsTask" ]
            ArchitectureRule.DomainJsonFree, typeof<JsonFromDomain>, [ "JsonDirect" ]
            ArchitectureRule.DomainJsonFree, typeof<JsonFromDomainTask>, [ "JsonTask" ]
            ArchitectureRule.Pure, typeof<AmbientShapes>, [ "AmbientDirect" ]
            ArchitectureRule.Pure, typeof<AmbientTaskShape>, [ "AmbientTask" ]
            ArchitectureRule.Pure, typeof<AmbientAsyncShape>, [ "AmbientAsync" ]
            ArchitectureRule.Pure, typeof<AmbientClosureShape>, [ "AmbientClosure" ]
            ArchitectureRule.Pure, typeof<AmbientTaskClosureShape>, [ "AmbientTaskClosure" ]
            ArchitectureRule.Postgres, typeof<PostgresShapes>, [ "PostgresDirect" ]
            ArchitectureRule.Postgres, typeof<PostgresTaskShape>, [ "PostgresTask" ]
            ArchitectureRule.DurableNames,
            typeof<VocabularyFromRecordFormat>,
            [ "VocabularyDirect" ]
            ArchitectureRule.DurableNames,
            typeof<VocabularyFromRecordFormatTask>,
            [ "VocabularyTask" ]
            ArchitectureRule.DurableNames, typeof<DurableShapes>, [ "DurableDirect" ]
            ArchitectureRule.DurableNames, typeof<DurableTaskShape>, [ "DurableTask" ]
            ArchitectureRule.Serializer, typeof<SerializerShapes>, [ "SerializerDirect" ]
            ArchitectureRule.Serializer, typeof<SerializerTaskShape>, [ "SerializerTask" ]
            ArchitectureRule.ServiceModelWithoutProtocol,
            typeof<ProtocolFromDomain>,
            [ "ProtocolDirect" ]
            ArchitectureRule.ServiceModelWithoutProtocol,
            typeof<ProtocolFromDomainTask>,
            [ "ProtocolTask" ]
            ArchitectureRule.VocabularyModuleNaming,
            nonNull (
                typeof<VocabularySample>
                    .Assembly
                    .GetType("ClaimCore.Tests.Architecture.NegativeFixtures.MisnamedVocabulary")
            ),
            [ "Direct" ]
            ArchitectureRule.VocabularyModuleNaming,
            nonNull (
                typeof<VocabularySample>
                    .Assembly
                    .GetType("ClaimCore.Tests.Architecture.NegativeFixtures.MisnamedTaskVocabulary")
            ),
            [ "TaskShape" ]
        ]

    for rule, fixture, members in cases do
        for memberName in members do
            let selected = shapeSubjects fixture memberName

            Expect.isNonEmpty
                (selected.GetObjects model.Value)
                ("Compiled negative shape is present: " + memberName)

            Expect.isFalse
                ((ApiRules.rule model.Value rule selected).HasNoViolations model.Value)
                (string rule + " detects " + memberName)

    // A label alone cannot prove coverage: each pair must violate its claimed target in isolation.
    let domainTargets =
        [
            {
                Assembly = "ClaimCore.Json"
                Direct = typeof<JsonFromDomain>
                DirectMember = "JsonDirect"
                Task = typeof<JsonFromDomainTask>
                TaskMember = "JsonTask"
            }
            {
                Assembly = "ClaimCore.Protocol"
                Direct = typeof<ProtocolFromDomain>
                DirectMember = "ProtocolDirect"
                Task = typeof<ProtocolFromDomainTask>
                TaskMember = "ProtocolTask"
            }
            {
                Assembly = "ClaimCore.Application"
                Direct = typeof<ApplicationFromDomain>
                DirectMember = "ApplicationDirect"
                Task = typeof<ApplicationFromDomainTask>
                TaskMember = "ApplicationTask"
            }
            {
                Assembly = "ClaimCore.RecordFormat"
                Direct = typeof<RecordFormatFromDomain>
                DirectMember = "RecordDirect"
                Task = typeof<RecordFormatFromDomainTask>
                TaskMember = "RecordTask"
            }
            {
                Assembly = "ClaimCore.Postgres"
                Direct = typeof<PostgresShapes>
                DirectMember = "PostgresDirect"
                Task = typeof<PostgresTaskShape>
                TaskMember = "PostgresTask"
            }

        ]

    Expect.equal
        (domainTargets |> List.map (fun target -> target.Assembly) |> List.sort)
        ([
            "ClaimCore.Json"
            "ClaimCore.Protocol"
            "ClaimCore.Application"
            "ClaimCore.RecordFormat"
            "ClaimCore.Postgres"
         ]
         |> List.sort)
        "A6 covers each forbidden assembly exactly once"

    let detectsTarget (target: string) fixture memberName =
        let selected = shapeSubjects fixture memberName

        Expect.isNonEmpty
            (selected.GetObjects model.Value)
            ("Compiled target control: " + memberName)

        let forbidden =
            Types().That().ResideInAssembly(Assembly.Load target)
            |> ApiRules.nonEmptyTargets model.Value target

        not (
            Types().That().Are(selected).Should().NotDependOnAny(forbidden).HasNoViolations
                model.Value
        )

    for target in domainTargets do
        let direct = target.Direct
        let directMember = target.DirectMember
        let task = target.Task
        let taskMember = target.TaskMember
        let target = target.Assembly

        for fixture, memberName in [ direct, directMember; task, taskMember ] do
            Expect.isTrue
                (detectsTarget target fixture memberName)
                (target + " is the actual compiled dependency")

            Expect.isFalse
                ((ApiRules.rule
                    model.Value
                    ArchitectureRule.DomainBoundary
                    (shapeSubjects fixture memberName))
                    .HasNoViolations
                    model.Value)
                ("A6 detects " + target + " through " + memberName)

    Expect.isFalse
        (detectsTarget "ClaimCore.Application" typeof<RecordFormatFromDomain> "RecordDirect")
        "A duplicate RecordFormat fixture cannot qualify Application coverage"

let private vocabularyModuleNaming () =
    for assembly in products.Value do
        check ArchitectureRule.VocabularyModuleNaming assembly

let private emptyTargets () =
    let missing = Types().That().HaveFullName("No.Such.ForbiddenTarget")

    Expect.throwsT<InvalidOperationException>
        (fun () -> ApiRules.nonEmptyTargets model.Value "missing control" missing |> ignore)
        "An empty forbidden provider fails independently of subject coverage"

let private emptySubjects () =
    let selected =
        ApiRules.subjects typeof<ClockCall>.Assembly
        |> fun subjects -> subjects.And().HaveFullName("No.Such.Subject")

    Expect.isFalse
        ((ApiRules.rule model.Value ArchitectureRule.Pure selected).HasNoViolations model.Value)
        "ArchUnitNET requires positive subjects by default"

let private productFriends () =
    let names =
        products.Value
        |> Array.map (fun assembly -> nonNull (assembly.GetName().Name))
        |> Set.ofArray

    let testAssemblies =
        Directory.EnumerateFiles(
            Path.Combine(root, "tests"),
            "*.fsproj",
            SearchOption.AllDirectories
        )
        |> Seq.map (Path.GetFileNameWithoutExtension >> nonNull)
        |> Set.ofSeq

    let allGrants =
        products.Value
        |> Seq.collect (fun assembly ->
            assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
            |> Seq.map (fun attribute ->
                nonNull (assembly.GetName().Name),
                nonNull (AssemblyName(attribute.AssemblyName).Name)))
        |> Set.ofSeq

    let grants = allGrants |> Set.filter (fun (_, target) -> Set.contains target names)

    for source, target in allGrants do
        Expect.isTrue
            (Set.contains target testAssemblies)
            (source + " may grant friendship only to a test assembly")

    Expect.isEmpty grants "No product assembly grants friendship to another product"

let private serviceModelWithoutProtocol () =
    check ArchitectureRule.ServiceModelWithoutProtocol (Assembly.Load "ClaimCore.Application")
    check ArchitectureRule.ServiceModelWithoutProtocol (Assembly.Load "ClaimCore.Postgres")

let private domainBoundary () =
    check ArchitectureRule.DomainBoundary (Assembly.Load "ClaimCore.Domain")

[<Tests>]
let tests =
    testList
        "architecture"
        [
            testCase
                "pure projects reject ambient effects including timezone and resources"
                ambientEffects
            testCase
                "durable formats reject Protocol and Domain vocabulary dependencies"
                durableCodecs
            testCase "JSON has no project dependencies" jsonDependencies
            testCase "F# compiled shapes reject every rule family" shapeControls
            testCase "empty subjects fail through ArchUnitNET" emptySubjects
            testCase "empty forbidden target sets fail explicitly" emptyTargets
            testCase "product friendship is limited to test assemblies" productFriends
            testCase "Domain excludes outer layers" domainBoundary
            testCase "hosts use only the service surface and ports" (fun () ->
                check ArchitectureRule.ServiceSurface (Assembly.Load "claimcore-server")
                check ArchitectureRule.ServiceSurface (Assembly.Load "claimcore-database"))
            testCase "Domain is independent of JSON" (fun () ->
                check ArchitectureRule.DomainJsonFree (Assembly.Load "ClaimCore.Domain"))
            testCase "vocabularies belong to their value type modules" vocabularyModuleNaming
            testCase
                "Application and Postgres use the service model without Protocol"
                serviceModelWithoutProtocol
            testCase "evaluated product edges and Postgres boundary" postgres
            testCase
                "published client closure rejects compiled server dependencies"
                publishedClosure
            testCase "JSON, Contract and RecordFormat avoid JsonSerializer" serializer
            testCase "closed wire vocabularies use the typed codec adapter" (fun () ->
                check ArchitectureRule.ClosedVocabularyCodecs (Assembly.Load "ClaimCore.Protocol")
                rejects ArchitectureRule.ClosedVocabularyCodecs typeof<DirectEnumeration>)
        ]
