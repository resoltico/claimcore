module ClaimCore.IntegrationTests.CatalogEpochTokenTests

open System.IO
open System.Text.RegularExpressions
open Npgsql
open Expecto
open ClaimCore.TestSupport
open ClaimCore.Witness
open ClaimCore.IntegrationTests.CatalogEpochChanges
open ClaimCore.IntegrationTests.Fixtures

// The catalog change token lets an admission skip its catalog-derived checks. These tests hold the
// token to that promise: every change those checks could notice must change it, and ordinary
// activity elsewhere must not.

let private execute (connection: NpgsqlConnection) sql =
    use command = new NpgsqlCommand(sql, connection)
    command.ExecuteNonQuery() |> ignore

let private tokenOf (connection: NpgsqlConnection) schema =
    match CatalogEpoch.readToken connection schema with
    | Some token -> token
    | None -> failtest "The catalog change token must be readable."

/// Apply the statements in a transaction that is always rolled back; the token before and after.
let private tokensAround (connectionString: string) schema (statements: string list) =
    use connection = new NpgsqlConnection(connectionString)
    connection.Open()
    use transaction = connection.BeginTransaction()

    try
        let before = tokenOf connection schema
        statements |> List.iter (execute connection)
        before, tokenOf connection schema
    finally
        transaction.Rollback()

let private namesWhere unchanged connectionString schema (changes: Change list) =
    changes
    |> List.filter (fun (_, statements) ->
        let before, after = tokensAround connectionString schema statements
        (before = after) = unchanged)
    |> List.map fst

let private everyStructuralChangeMovesTheToken =
    testCase
        "[CC-DB-001] every structural or privilege change moves the primary catalog token"
        (fun () ->
            let stale = namesWhere true (adminConnection ()) "claimcore" (primary ())

            Expect.isEmpty
                stale
                "A change the admission checks could notice left the token unchanged, so a guarded checkout would skip a check it must run")

let private unrelatedActivityKeepsTheToken =
    testCase
        "[CC-DB-001] comments, statistics, temporary and unrelated objects keep the token"
        (fun () ->
            let moved = namesWhere false (adminConnection ()) "claimcore" neutral

            Expect.isEmpty
                moved
                "Activity outside the checked catalog would force needless full verification")

let private tokenIsStableWithoutChanges =
    testCase "[CC-DB-001] repeated reads of an unchanged catalog agree" (fun () ->
        use connection = new NpgsqlConnection(adminConnection ())
        connection.Open()
        let first = tokenOf connection "claimcore"
        let second = tokenOf connection "claimcore"
        Expect.equal second first "An unchanged catalog has one token")

let private witnessChangesMoveTheToken =
    testCase
        "[CC-WIT-001] structural and privilege changes move the witness catalog token"
        (fun () ->
            let stale = namesWhere true (witnessOwnerConnection ()) "claimcore_witness" witness
            Expect.isEmpty stale "A witness catalog change left the token unchanged")

let private guardedFiles =
    [
        "db/catalog-manifest.sql"
        "db/witness-catalog.sql"
        "db/witness-admission-structure.sql"
        "db/witness-audit-admission-structure.sql"
        "src/ClaimCore.Postgres/RuntimeAccessPolicy.fs"
        "src/ClaimCore.Postgres/RuntimeSchema.fs"
        "src/ClaimCore.Postgres/RuntimeConstraintPolicy.fs"
    ]

let private systemCatalogs () =
    use connection = new NpgsqlConnection(adminConnection ())
    connection.Open()

    use listing =
        new NpgsqlCommand(
            "SELECT relname FROM pg_catalog.pg_class WHERE relnamespace = 'pg_catalog'::regnamespace AND relkind IN ('r', 'v')",
            connection
        )

    use reader = listing.ExecuteReader()

    [
        while reader.Read() do
            reader.GetString(0)
    ]
    |> Set.ofList

/// Reading a catalog the token ignores would let a change to it slip past a guarded checkout.
let private tokenCoversEveryCatalogAdmissionReads =
    testCase "[CC-DB-001] the token names every catalog the guarded checks read" (fun () ->
        let root = RepositoryRoot.find ()

        let text (relative: string) =
            File.ReadAllText(Path.Combine(root, relative))

        let catalogs = systemCatalogs ()

        let named (source: string) =
            Regex.Matches(source, @"\bpg_[a-z_]+\b")
            |> Seq.map (fun found -> found.Value)
            |> Seq.filter catalogs.Contains
            |> Set.ofSeq

        let covered = named (text "db/catalog-epoch.sql")

        let uncovered =
            guardedFiles
            |> List.collect (fun path ->
                Set.difference (named (text path)) covered
                |> Set.toList
                |> List.map (fun name -> $"{path}: {name}"))

        Expect.isEmpty
            uncovered
            "Add the catalog to db/catalog-epoch.sql and to its sensitivity tests")

let tests =
    testList
        "catalog change token"
        [
            everyStructuralChangeMovesTheToken
            unrelatedActivityKeepsTheToken
            tokenIsStableWithoutChanges
            witnessChangesMoveTheToken
            tokenCoversEveryCatalogAdmissionReads
        ]
