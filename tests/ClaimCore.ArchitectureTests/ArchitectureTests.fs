module ClaimCore.ArchitectureTests.ArchitectureTests

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

let private actualGraph () =
    projects.Value |> Map.map (fun _ path -> Manifest.references path)

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
        testCase
            "[CC-ARCH-001] declared project references equal the manifest in both directions"
            (fun () -> ProjectGraph.check contract.Value (actualGraph ()))
        testCase "a missing required edge identifies its project" (fun () ->
            let changed = actualGraph () |> Map.add "ClaimCore.Application" Set.empty

            reject
                (fun () -> ProjectGraph.check contract.Value changed)
                "ClaimCore.Application projectGraph")
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
        testCase ("compiled negative " + fixture.Name) (fun () ->
            let subjects = Types().That().HaveFullName(fixture.FullName |> nonNull)

            Expect.isFalse
                (subjects.GetObjects model.Value |> Seq.isEmpty)
                "Compiled negative fixture is present"

            reject
                (fun () -> ApiRules.check model.Value name "ClaimCore.Domain" subjects)
                ("ClaimCore.Domain " + name)))

[<Tests>]
let tests = testList "architecture" (graphTests @ positiveRules @ negativeRules)
