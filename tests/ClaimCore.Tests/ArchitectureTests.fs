module ClaimCore.Tests.ArchitectureTests

open System
open System.IO
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.TestSupport

let private references (assembly: Reflection.Assembly) =
    assembly.GetReferencedAssemblies()
    |> Array.map (fun reference -> nonNull reference.Name)
    |> Set.ofArray

let private expectAbsent assembly forbidden =
    let actual = references assembly

    forbidden
    |> List.iter (fun name ->
        Expect.isFalse (Set.contains name actual) ("Forbidden dependency: " + name))

let private dependencyTests =
    testList
        "dependency direction"
        [
            testCase "Domain has no adapter or infrastructure dependency" (fun () ->
                expectAbsent
                    typeof<Claim>.Assembly
                    [
                        "ClaimCore.Application"
                        "ClaimCore.Contracts"
                        "ClaimCore.Postgres"
                        "ClaimCore.Cli"
                        "Npgsql"
                        "System.Text.Json"
                    ])
            testCase "Application has no transport, contract, or PostgreSQL dependency" (fun () ->
                expectAbsent
                    typeof<IClaimsCore>.Assembly
                    [
                        "ClaimCore.Contracts"
                        "ClaimCore.Postgres"
                        "ClaimCore.Cli"
                        "Npgsql"
                        "System.Text.Json"
                    ])
            testCase "Contracts projects semantic values without adapter dependencies" (fun () ->
                expectAbsent
                    typeof<ContractModel>.Assembly
                    [ "ClaimCore.Postgres"; "ClaimCore.Cli"; "Npgsql"; "Microsoft.AspNetCore.App" ])
        ]

let private applicationSurfaceIsClosed () =
    let exported =
        typeof<IClaimsCore>.Assembly.GetExportedTypes()
        |> Array.map (fun value -> nonNull value.FullName)
        |> Set.ofArray

    for forbidden in
        [
            "ClaimCore.Application.IClaimStore"
            "ClaimCore.Application.IRecoveryStore"
            "ClaimCore.Application.CoreApi"
            "ClaimCore.Application.PreparedOperation"
            "ClaimCore.Application.RetainedPreparation"
            "ClaimCore.Application.RegisterDefinition"
        ] do
        Expect.isFalse (Set.contains forbidden exported) ("Public bypass: " + forbidden)

    Expect.isFalse
        (exported
         |> Set.exists (fun name ->
             name.StartsWith("ClaimCore.Application.V2", StringComparison.Ordinal)))
        "The typed API has no transitional namespace"

let private postgresRecoverySeamIsSingular () =
    let postgresTypes =
        typeof<Runtime>.Assembly.GetTypes()
        |> Array.map (fun value -> nonNull value.FullName)
        |> Set.ofArray

    for removed in
        [
            "ClaimCore.Postgres.IRequestPreparationService"
            "ClaimCore.Postgres.PostgresPreparationService"
            "ClaimCore.Hosting.PostgresRecoveryPort"
        ] do
        Expect.isFalse (Set.contains removed postgresTypes) ("Superseded recovery seam: " + removed)

let private transportCodecsAreSingular () =
    let root = RepositoryRoot.find ()
    let cli = Path.Combine(root, "src/ClaimCore.Cli")
    let web = Path.Combine(root, "src/ClaimCore.Web")

    for removed in
        [
            "JsonViews.fs"
            "JsonOutcomeQueries.fs"
            "JsonOutcomeMutations.fs"
            "JsonOutcomeImports.fs"
        ] do
        Expect.isFalse (File.Exists(Path.Combine(cli, removed))) ("Removed CLI codec: " + removed)

    let webSource =
        Directory.GetFiles(web, "*.fs", SearchOption.AllDirectories)
        |> Array.map File.ReadAllText
        |> String.concat "\n"

    Expect.isFalse
        (webSource.Contains("{|", StringComparison.Ordinal))
        "No anonymous Web JSON records"

    Expect.isFalse
        (webSource.Contains("Results.Json", StringComparison.Ordinal))
        "No adapter JSON codec"

    let dispatch = File.ReadAllText(Path.Combine(cli, "EndpointDispatch.fs"))
    Expect.stringContains dispatch "CliWireCodec" "CLI delegates encoding to Contracts"

    Expect.isFalse
        (dispatch.Contains("Utf8JsonWriter", StringComparison.Ordinal))
        "No CLI endpoint codec"

let private historicalSurfacesHaveNoActions () =
    for historical in [ typeof<OperationReceipt>; typeof<HistoryEntry>; typeof<CaseSummary> ] do
        let properties = historical.GetProperties() |> Array.map _.Name |> Set.ofArray

        Expect.isFalse
            (properties.Contains("AvailableCommands"))
            ("Historical/advisory actions are absent: " + historical.Name)

let private coreMethodShapes () =
    let methods =
        typeof<IClaimsCore>.GetMethods()
        |> Array.map (fun methodInfo -> methodInfo.Name, methodInfo)

    let methodInfo name =
        methods |> Array.find (fst >> (=) name) |> snd

    Expect.equal
        (methodInfo "Describe").ReturnType
        typeof<CoreDescription>
        "Describe is synchronous"

    for name in [ "Prepare"; "Execute"; "Get"; "List"; "History"; "ObserveOperation" ] do
        let result = (methodInfo name).ReturnType

        Expect.isTrue
            (result.IsGenericType
             && result.GetGenericTypeDefinition() = typedefof<Threading.Tasks.Task<_>>)
            (name + " is task-based")

let private surfaceTests =
    testList
        "typed core public surface"
        [
            testCase
                "IClaimsCore exposes only typed endpoint methods and recovery workflow"
                (fun () ->
                    let members =
                        typeof<IClaimsCore>.GetMethods() |> Array.map _.Name |> Set.ofArray

                    Expect.equal
                        members
                        (Set.ofList
                            [
                                "Describe"
                                "Prepare"
                                "Execute"
                                "Get"
                                "List"
                                "History"
                                "ObserveOperation"
                                "get_Recovery"
                            ])
                        "No catch-all query or store escape hatch"

                    coreMethodShapes ()
                    historicalSurfacesHaveNoActions ())
            testCase
                "storage ports and preparation records are not public application types"
                (fun () ->
                    applicationSurfaceIsClosed ()
                    postgresRecoverySeamIsSingular ())
            testCase
                "transport codecs have one Contracts owner and no anonymous adapter projection"
                transportCodecsAreSingular
        ]

let tests =
    testList "core ownership and typed interfaces" [ dependencyTests; surfaceTests ]
