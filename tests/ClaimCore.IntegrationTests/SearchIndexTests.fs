module ClaimCore.IntegrationTests.SearchIndexTests

open System
open System.IO
open System.Security.Cryptography
open Expecto
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private read =
    function
    | Outcome.Succeeded value -> value
    | other -> failtestf "Expected a successful search fixture read: %A" other

let private readRows source statement parameters =
    DatabaseSession.read source token (fun connection ->
        DatabaseSession.rows connection statement parameters token)

let private execute (source: NpgsqlDataSource) statement parameters =
    task {
        use! connection = source.OpenConnectionAsync()
        let! _ = DatabaseSession.execute connection statement parameters token
        return ()
    }

let private installedIndexes source =
    task {
        let! rows =
            readRows
                source
                "SELECT x.relname,a.attname,m.amname,n.nspname,o.opcname,i.indpred IS NULL,i.indisvalid FROM pg_index i JOIN pg_class x ON x.oid=i.indexrelid JOIN pg_am m ON m.oid=x.relam JOIN pg_opclass o ON o.oid=i.indclass[0] JOIN pg_namespace n ON n.oid=o.opcnamespace JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=i.indkey[0] WHERE x.oid IN ('claimcore.cases_search'::regclass,'claimcore.cases_work_search'::regclass) ORDER BY x.relname"
                [||]

        let actual =
            rows
            |> List.map (fun row ->
                DatabaseSession.value<string> 0 row,
                DatabaseSession.value<string> 1 row,
                DatabaseSession.value<string> 2 row,
                DatabaseSession.value<string> 3 row,
                DatabaseSession.value<string> 4 row,
                DatabaseSession.value<bool> 5 row,
                DatabaseSession.value<bool> 6 row)

        Expect.equal
            actual
            [
                "cases_search", "search_text", "gin", "public", "gin_trgm_ops", true, true
                "cases_work_search", "work_search_text", "gin", "public", "gin_trgm_ops", true, true
            ]
            "Both complete valid indexes use the pinned module's actual operator class"
    }

let private freshOwner () =
    async {
        let database = PostgresFixture.empty ()

        try
            do! database.StartAsync() |> Async.AwaitTask
            use bootstrap = NpgsqlDataSource.Create(database.GetConnectionString())

            do!
                execute
                    bootstrap
                    "CREATE ROLE claimcore_schema_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD 'synthetic-owner-only'; CREATE ROLE claimcore_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT PASSWORD 'synthetic-runtime-only'; ALTER DATABASE postgres OWNER TO claimcore_schema_owner"
                    [||]
                |> Async.AwaitTask

            let ownerSettings = NpgsqlConnectionStringBuilder(database.GetConnectionString())
            ownerSettings.Username <- "claimcore_schema_owner"
            ownerSettings.Password <- "synthetic-owner-only"
            use owner = NpgsqlDataSource.Create(ownerSettings.ConnectionString)

            let! installed =
                SchemaAdministration.initialize
                    owner
                    "Etc/UTC"
                    (fun _ _ -> System.Threading.Tasks.Task.FromResult(()))
                    token
                |> Async.AwaitTask

            let! repeated =
                task {
                    try
                        let! _ =
                            SchemaAdministration.initialize
                                owner
                                "Etc/UTC"
                                (fun _ _ -> System.Threading.Tasks.Task.FromResult(()))
                                token

                        return false
                    with :? System.InvalidOperationException ->
                        return true
                }
                |> Async.AwaitTask

            Expect.isTrue
                repeated
                "A nonempty installation is refused, including with the module already installed"

            let! metadata =
                readRows
                    owner
                    "SELECT e.extversion,n.nspname,current_user,r.rolsuper,b.identifier,b.script_sha256 FROM pg_extension e JOIN pg_namespace n ON n.oid=e.extnamespace CROSS JOIN pg_roles r CROSS JOIN claimcore.baseline_identity b WHERE e.extname='pg_trgm' AND r.rolname=current_user"
                    [||]
                |> Async.AwaitTask

            let row = List.exactlyOne metadata
            Expect.equal (DatabaseSession.value<string> 0 row) "1.6" "Pinned module version"

            Expect.equal
                (DatabaseSession.value<string> 1 row)
                "public"
                "Explicit extension namespace"

            Expect.equal
                (DatabaseSession.value<string> 2 row)
                "claimcore_schema_owner"
                "Actual owner connection"

            Expect.isFalse
                (DatabaseSession.value<bool> 3 row)
                "A superuser is not required to initialize"

            Expect.equal
                (DatabaseSession.value<string> 4 row)
                Baseline.identifier
                "Supported baseline identity"

            let expectedDigest =
                File.ReadAllBytes(Path.Combine(RepositoryRoot.find (), "db/baseline.sql"))
                |> SHA256.HashData

            Expect.sequenceEqual
                (DatabaseSession.value<byte array> 5 row)
                expectedDigest
                "The installed checksum covers the delivered extension and index DDL"

            do! installedIndexes owner |> Async.AwaitTask
            let runtimeSettings = NpgsqlConnectionStringBuilder(ownerSettings.ConnectionString)
            runtimeSettings.Username <- "claimcore_runtime"
            runtimeSettings.Password <- "synthetic-runtime-only"
            use runtimeSource = NpgsqlDataSource.Create(runtimeSettings.ConnectionString)

            let! admitted =
                Runtime.openConnection runtimeSettings.ConnectionString token |> Async.AwaitTask

            match admitted with
            | Ok runtime -> runtime.Dispose()
            | Error error -> failtestf "Least-privilege runtime admission failed: %A" error

            let! privileges =
                readRows
                    runtimeSource
                    "SELECT r.rolsuper OR r.rolcreatedb OR r.rolcreaterole OR r.rolreplication OR r.rolbypassrls,has_database_privilege(current_user,current_database(),'CREATE'),has_schema_privilege(current_user,'public','CREATE'),has_table_privilege(current_user,'claimcore.cases','SELECT') FROM pg_roles r WHERE r.rolname=current_user"
                    [||]
                |> Async.AwaitTask

            let privileges = List.exactlyOne privileges

            for index in 0..2 do
                Expect.isFalse
                    (DatabaseSession.value<bool> index privileges)
                    "Runtime gains no administrative privilege"

            Expect.isTrue
                (DatabaseSession.value<bool> 3 privileges)
                "Existing projection reads remain available"

            let! denied =
                task {
                    try
                        do!
                            execute
                                runtimeSource
                                "CREATE INDEX denied_runtime_index ON claimcore.cases(case_id)"
                                [||]

                        return None
                    with :? PostgresException as error ->
                        return Some error.SqlState
                }
                |> Async.AwaitTask

            Expect.equal denied (Some "42501") "Runtime still cannot perform index DDL"

            do!
                execute
                    owner
                    "UPDATE claimcore.baseline_identity SET script_sha256=decode(repeat('00',32),'hex')"
                    [||]
                |> Async.AwaitTask

            let! rejected =
                Runtime.openConnection runtimeSettings.ConnectionString token |> Async.AwaitTask

            match rejected with
            | Error RuntimeOpenFault.SchemaMismatch -> ()
            | Ok runtime ->
                runtime.Dispose()
                failtest "Changed index-baseline checksum was admitted"
            | Error error -> failtestf "Expected checksum refusal, got %A" error

            let! ownerRefused =
                task {
                    try
                        let! _ =
                            SchemaAdministration.initialize
                                owner
                                "Etc/UTC"
                                (fun _ _ -> System.Threading.Tasks.Task.FromResult(()))
                                token

                        return false
                    with :? InvalidOperationException ->
                        return true
                }
                |> Async.AwaitTask

            Expect.isTrue
                ownerRefused
                "Owner initialization refuses the changed checksum without adopting it"

            do! installedIndexes owner |> Async.AwaitTask

            do!
                execute
                    owner
                    "UPDATE claimcore.baseline_identity SET script_sha256=$1"
                    [| box expectedDigest |]
                |> Async.AwaitTask

            let! restored =
                readRows owner "SELECT installation_id FROM claimcore.installation" [||]
                |> Async.AwaitTask

            Expect.equal
                (DatabaseSession.value<Guid> 0 (List.exactlyOne restored))
                installed
                "Restoring the checksum retains installation identity"

            let! reopened =
                Runtime.openConnection runtimeSettings.ConnectionString token |> Async.AwaitTask

            match reopened with
            | Ok runtime -> runtime.Dispose()
            | Error error -> failtestf "Restored checksum must reopen unchanged: %A" error
        finally
            database.DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

let private literalLifecycle (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let needle = "needle%_\\[.*]***="
        let authored = "ＮＥＥＤＬＥ%_\\[.*]***="

        for operation, reference, claimant, description in
            [
                100, "GIN-FACTS", "Financial claimant", Some("Incident " + authored + "\nالعربية")
                101, "GIN-WORK", "Work claimant", None
                102, "GIN-PLAIN", "Ordinary claimant", None
            ] do
            let registration =
                Inputs.registrationWith
                    ([
                        BusinessField.ClaimantName, claimant
                        BusinessField.ClaimedAmount, "500"
                        BusinessField.ClaimedCurrency, "EUR"
                     ]
                     @ (description
                        |> Option.map (fun value -> BusinessField.IncidentDescription, value)
                        |> Option.toList))
                    registration

            let! opened =
                Service.execute
                    actor
                    (Inputs.caseOperation (
                        request
                            database.Fixture
                            operation
                            reference
                            0L
                            (AuthoredCaseAct.Open registration)
                    ))
                    token

            accepted opened |> ignore

        let! handled =
            Service.execute
                actor
                (OperationInput.Work
                    {
                        OperationId = id database.Fixture 103
                        Target = CaseSelector.Reference "GIN-WORK"
                        ExpectedRevision = 0L
                        Act =
                            WorkAct.UpdateWork
                                {
                                    Summary = WorkValue.Set("Handling " + authored)
                                    Responsible = WorkValue.Keep
                                }
                    })
                token

        accepted handled |> ignore

        let filter =
            { CaseListFilter.empty<string> with
                Text = Some needle
            }

        let pageRequest =
            {
                CaseListRequest.Filter = filter
                Page = { AfterCursor = None; Limit = 50 }
            }

        let references (page: CaseSummaryPage) =
            page.Items |> List.map (fun item -> CaseReference.format item.Reference)

        let! before = Service.list actor pageRequest token

        Expect.equal
            (read before |> references)
            [ "GIN-FACTS"; "GIN-WORK" ]
            "Both independent text owners match literal normalized input; the plain case does not"

        let! totals = Service.caseSummary actor filter token
        let totals = read totals
        Expect.equal totals.Counts.Total 2L "Search selects each financial parent once"

        Expect.equal
            (totals.Claimed
             |> List.map (fun value ->
                 Currency.format value.Currency, DecimalTotal.format value.Amount))
            [ "EUR", "1000" ]
            "Exact money remains independent of indexes"

        let! granted =
            grant
                actor
                (id database.Fixture 104)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople granted
        let! context = Service.context actor () token
        let ownerId = (Inputs.activeContext (read context)).ActorId

        let query observed =
            {
                CasePageQuery.Selection =
                    {
                        DateBasis =
                            {
                                ObservedAt = observed
                                BusinessDate = database.Fixture.Calendar.DateAt observed
                                TimeZoneId = database.Fixture.Calendar.TimeZoneId
                            }
                        Visibility = CaseVisibility.Every
                        ActorId = ownerId
                        ReadRoles = Access.readRoles database.Fixture.Owner
                        ForbiddenRoles = Access.invalidRoles database.Fixture.Owner
                        Filter =
                            CaseListFilter.admit filter |> Result.defaultWith (string >> failtest)
                    }
                AfterPosition = None
                Limit = 50
            }

        let selected (page: ClaimCore.Application.Page<CasePageItem, CaseListPosition>) =
            page.Items
            |> List.map (fun item -> CaseReference.format item.Case.Snapshot.Reference)

        let! retained =
            database.Fixture.Store.Read
                {
                    CancellationToken = token
                    Work =
                        fun tx ->
                            task {
                                let! old = tx.CasePage(query tx.ObservedAt)

                                Expect.equal
                                    (selected old)
                                    [ "GIN-FACTS"; "GIN-WORK" ]
                                    "Positive old-snapshot control"

                                let! erased =
                                    Service.execute
                                        actor
                                        (Inputs.caseOperation (
                                            request
                                                database.Fixture
                                                105
                                                "GIN-FACTS"
                                                1L
                                                (AuthoredCaseAct.Bare BareAct.Erase)
                                        ))
                                        token

                                accepted erased |> ignore
                                let! stillOld = tx.CasePage(query tx.ObservedAt)

                                Expect.equal
                                    (selected stillOld)
                                    (selected old)
                                    "An existing reader keeps its consistent snapshot through erasure"
                            }
                }

        Expect.isOk retained "The owned old read completes"
        let! erasedPage = Service.list actor pageRequest token

        Expect.equal
            (read erasedPage |> references)
            [ "GIN-WORK" ]
            "A fresh reader cannot search erased narrative content"

        let! checkpoint = checkpoint database
        do! enterMaintenance database

        do!
            sql
                database
                "UPDATE claimcore.cases SET search_text='wrong financial projection',work_search_text='wrong work projection' WHERE case_reference=$1"
                [| box "GIN-WORK" |]

        let! rebuilt = ProjectionRebuild.rebuild database.OwnerSource checkpoint token

        Expect.isOk
            rebuilt
            "Rebuild replaces corrupted derived text while native indexes remain installed"

        let! rebuiltPage = Service.list actor pageRequest token

        Expect.equal
            (read rebuiltPage |> references)
            [ "GIN-WORK" ]
            "Rebuild restores live work search without resurrecting erased content"

        do! installedIndexes database.OwnerSource
        let! _ = verified database
        return ()
    }

let private registrationIdentifiers (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let reference = "POLICY-SEARCH"

        let write number revision act =
            Service.execute
                actor
                (Inputs.caseOperation (request database.Fixture number reference revision act))
                token

        let search text =
            task {
                let! reply =
                    Service.list
                        actor
                        {
                            Filter =
                                { CaseListFilter.empty<string> with
                                    Text = Some text
                                }
                            Page = { AfterCursor = None; Limit = 20 }
                        }
                        token

                return (read reply).Items |> List.map (_.Reference >> CaseReference.format)
            }

        let facts =
            Inputs.registrationWith
                [
                    BusinessField.PolicyNumber, "POLICY-UNIQUE-42"
                    BusinessField.InsurerClaimReference, "INSURER-UNIQUE-17"
                ]
                registration

        let! opened = write 700 0L (CaseAct.Open facts)
        accepted opened |> ignore

        for term in [ "policy-unique-42"; "insurer-unique-17" ] do
            let! found = search term

            Expect.equal
                found
                [ reference ]
                "The declared optional fact reaches the real search projection"

        let update members =
            RegistrationUpdateInput.create members |> Result.defaultWith failtest

        let! amended =
            write
                701
                1L
                (CaseAct.AmendRegistration(
                    update [ BusinessField.PolicyNumber, Some "POLICY-UPDATED-43" ]
                ))

        accepted amended |> ignore
        let! oldPolicy = search "policy-unique-42"
        let! newPolicy = search "policy-updated-43"
        Expect.isEmpty oldPolicy "Amendment replaces searchable policy text"
        Expect.equal newPolicy [ reference ] "Replacement policy is searchable"
        let! decided = write 702 2L (CaseAct.Decide Inputs.decision)
        accepted decided |> ignore

        let! corrected =
            write
                703
                3L
                (CaseAct.Correct
                    {
                        Registration =
                            Amendment.Update(
                                update
                                    [
                                        BusinessField.InsurerClaimReference,
                                        Some "INSURER-UPDATED-18"
                                    ]
                            )
                        Decision = Change.Keep
                    })

        accepted corrected |> ignore
        let! correctedReference = search "insurer-updated-18"
        Expect.equal correctedReference [ reference ] "Correction updates the same projection"

        let! steward =
            grant
                actor
                (id database.Fixture 704)
                database.Fixture.Owner
                {
                    Role = Role.DataSteward
                    Scope = GrantTarget.Installation
                }
                true

        acceptedPeople steward
        let! erased = write 705 4L (CaseAct.Bare BareAct.Erase)
        accepted erased |> ignore

        for term in [ "policy-updated-43"; "insurer-updated-18" ] do
            let! found = search term
            Expect.isEmpty found "Erased identifiers cannot be searched"

        let! _ = verified database
        return ()
    }

[<Tests>]
let tests =
    testSequenced
    <| testList
        "literal search indexes"
        [
            testCaseAsync
                "declared policy identifiers follow open amendment correction search and erasure"
                (run registrationIdentifiers)
            testCaseAsync
                "fresh pinned module installation refuses repeated initialization for a nonsuper owner and preserves runtime limits"
                (freshOwner ())
            testCaseAsync
                "literal financial and work search survives erasure snapshots and projection rebuild"
                (run literalLifecycle)
        ]
