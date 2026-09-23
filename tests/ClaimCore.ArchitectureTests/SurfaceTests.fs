module ClaimCore.ArchitectureTests.SurfaceTests

open System
open Expecto

let private exported name =
    ProductModel.assemblies.Value
    |> Array.find (fun candidate -> candidate.GetName().Name = name)
    |> _.GetExportedTypes()
    |> Array.map (fun value -> nonNull value.FullName)
    |> Set.ofArray

/// The composition root publishes one entry point. Everything it binds together - the store, the
/// recovery port, the data source and the business clock - stays inaccessible to its callers.
let private compositionSurfaceIsSingular () =
    Expect.equal
        (exported "ClaimCore.Hosting")
        (Set.singleton "ClaimCore.Hosting.Runtime")
        "The composition root exports exactly one entry point"

/// PostgreSQL publishes only the schema-owner administration surface that the Database executable
/// operates. No store, port, connection, or row type is reachable from any other component.
let private storageSurfaceIsAdministrationOnly () =
    Expect.equal
        (exported "ClaimCore.Postgres")
        (Set.ofList
            [
                "ClaimCore.Postgres.AdministrationFailure"
                "ClaimCore.Postgres.AdministrationFailure+Tags"
                "ClaimCore.Postgres.AdministrationOutcome"
                "ClaimCore.Postgres.AdministrationOutcome`1"
                "ClaimCore.Postgres.AdministrationOutcome`1+Completed"
                "ClaimCore.Postgres.AdministrationOutcome`1+CompletedCleanupFailed"
                "ClaimCore.Postgres.AdministrationOutcome`1+CompletionUnknown"
                "ClaimCore.Postgres.AdministrationOutcome`1+NotCommitted"
                "ClaimCore.Postgres.AdministrationOutcome`1+NotStarted"
                "ClaimCore.Postgres.AdministrationOutcome`1+Tags"
                "ClaimCore.Postgres.Baseline"
                "ClaimCore.Postgres.SchemaBaseline"
                "ClaimCore.Postgres.PreparationPruneOptions"
                "ClaimCore.Postgres.PreparationPruneOptionsModule"
                "ClaimCore.Postgres.PreparationPruneResult"
                "ClaimCore.Postgres.PreparationPruning"
                "ClaimCore.Postgres.UnsupportedPostgresVersion"
            ])
        "Storage exports exactly the reviewed schema-owner administration surface"

/// Application's storage ports and preparation records are the seam the whole architecture rests
/// on. None of them may become part of its public surface.
let private applicationSurfaceIsClosed () =
    let surface = exported "ClaimCore.Application"

    for forbidden in
        [
            "ClaimCore.Application.IClaimStore"
            "ClaimCore.Application.IRecoveryStore"
            "ClaimCore.Application.IBusinessTime"
            "ClaimCore.Application.CoreApi"
            "ClaimCore.Application.PreparedOperation"
            "ClaimCore.Application.RetainedPreparation"
        ] do
        Expect.isFalse (Set.contains forbidden surface) ("Public bypass: " + forbidden)

/// The protocol layer declares the seam the composition root fills, so the seam itself must stay
/// public while no runtime factory reaches it.
let private protocolDeclaresItsOwnSeam () =
    let surface = exported "ClaimCore.CliProtocol"

    for required in [ "ClaimCore.Cli.ICoreSupplier"; "ClaimCore.Cli.CoreUnavailable" ] do
        Expect.isTrue (Set.contains required surface) ("Protocol seam is public: " + required)

    Expect.isFalse
        (surface
         |> Set.exists (fun name -> name.StartsWith("ClaimCore.Hosting", StringComparison.Ordinal)))
        "No runtime factory type reaches the protocol surface"

let tests =
    testList
        "published component surface"
        [
            testCase "the composition root exports one entry point" compositionSurfaceIsSingular
            testCase
                "storage exports only schema-owner administration"
                storageSurfaceIsAdministrationOnly
            testCase "application storage ports stay private" applicationSurfaceIsClosed
            testCase "the CLI protocol declares its own core seam" protocolDeclaresItsOwnSeam
        ]
