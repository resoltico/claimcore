module ClaimCore.IntegrationTests.StorageFailurePhaseTests

open System
open System.Diagnostics
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Application.Contracts
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Protocol
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.TestSupport.StoreFixtures
open ClaimCore.IntegrationTests.VerificationSupport

let private sensitive =
    "synthetic-sensitive-principal-reference-request-credential-payload"

let private controls (database: PostgresFixture.Database) =
    task {
        let! actor = caseWorker database.Fixture
        let! checkpoint = verified database
        use activity = new Activity("ClaimCore.Storage.FailurePhaseControl")
        activity.Start() |> ignore
        let observed = ConcurrentQueue<FailureDiagnostic>()

        use _subscription =
            FailureDiagnostics.listener.Subscribe(
                { new IObserver<KeyValuePair<string, obj>> with
                    member _.OnNext value =
                        match Activity.Current with
                        | null -> ()
                        | current when current.TraceId = activity.TraceId ->
                            observed.Enqueue(unbox value.Value)

                            raise (
                                InvalidOperationException "synthetic throwing diagnostic observer"
                            )
                        | _ -> ()

                    member _.OnError _ = ()
                    member _.OnCompleted() = ()
                }
            )

        use cancelled = new CancellationTokenSource()
        cancelled.Cancel()

        let! unopened =
            Transactions.read database.RuntimeSource cancelled.Token (fun _ _ -> Task.FromResult 0)

        Expect.equal
            unopened
            (Error StoreFailure.Cancelled)
            "Pre-work cancellation is definite and does not invoke work"

        let! read =
            Transactions.read database.RuntimeSource token (fun connection _ ->
                DatabaseSession.rows connection ("SELECT '" + sensitive + "'::integer") [||] token)

        Expect.equal
            (read |> Result.map ignore)
            (Error StoreFailure.Unavailable)
            "Real provider read failure is mapped before exception details reach callers"

        let! internalFailure =
            Transactions.read database.RuntimeSource token (fun _ _ ->
                Task.FromException<int>(InvalidOperationException sensitive))

        Expect.equal
            internalFailure
            (Error StoreFailure.Internal)
            "An unknown internal failure retains the internal fault classification"

        do!
            sql
                database
                ("CREATE FUNCTION public.fail_storage_phase() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION '"
                 + sensitive
                 + "' USING DETAIL='"
                 + sensitive
                 + "'; RETURN NEW; END $$")
                [||]

        let execute number =
            Service.execute
                actor
                (Inputs.caseOperation (
                    request number sensitive 0L (AuthoredCaseAct.Open registration)
                ))
                token

        let safe (reply: ExecuteReply) =
            Expect.equal
                reply.Outcome
                (ExecuteResult.Failed CoreFault.StoreUnavailable)
                "The supported business result remains a generic fault"

            let wire =
                Codec.encode Api.operationExecute.Output reply |> Text.Encoding.UTF8.GetString

            Expect.isFalse
                (wire.Contains(sensitive, StringComparison.Ordinal))
                "No request or provider canary reaches the public fault"

        do!
            sql
                database
                "CREATE TRIGGER fail_storage_phase BEFORE INSERT ON claimcore.ledger FOR EACH ROW EXECUTE FUNCTION public.fail_storage_phase()"
                [||]

        let! append = execute 910
        safe append
        do! sql database "DROP TRIGGER fail_storage_phase ON claimcore.ledger" [||]

        do!
            sql
                database
                "CREATE TRIGGER fail_storage_phase BEFORE INSERT ON claimcore.cases FOR EACH ROW EXECUTE FUNCTION public.fail_storage_phase()"
                [||]

        let! projection = execute 911
        safe projection
        do! sql database "DROP TRIGGER fail_storage_phase ON claimcore.cases" [||]

        do!
            sql
                database
                "CREATE CONSTRAINT TRIGGER fail_storage_phase AFTER INSERT ON claimcore.ledger DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fail_storage_phase()"
                [||]

        let! commit = execute 912
        safe commit
        do! sql database "DROP TRIGGER fail_storage_phase ON claimcore.ledger" [||]
        let! unchanged = verified database

        Expect.equal
            unchanged
            checkpoint
            "Read append projection and definite server commit refusals leave no accepted effect"

        let evidence = observed.ToArray()

        Expect.equal
            (evidence |> Array.map _.Phase |> Set.ofArray)
            (Set.ofList[FailurePhase.Open
                        FailurePhase.Read
                        FailurePhase.Append
                        FailurePhase.Projection
                        FailurePhase.Commit])
            "Real failure phases survive broad public mapping"

        Expect.equal
            (evidence |> Array.map _.Correlation |> Set.ofArray |> Set.count)
            6
            "Independent operations have independent internal correlation"

        for diagnostic in evidence do
            Expect.notEqual diagnostic.Correlation Guid.Empty "Bounded internal identity"
            Expect.isLessThanOrEqual diagnostic.Methods.Length 8 "Bounded stack frames"

        Expect.isTrue
            (evidence
             |> Array.exists (fun value ->
                 value.Classification = FailureClass.Internal && value.Methods.Length > 0))
            "Unknown failures retain bounded adapter method evidence"

        Expect.isFalse
            ((sprintf "%A" evidence).Contains(sensitive, StringComparison.Ordinal))
            "Messages SQL provider details and business canaries are absent from diagnostics"

        let! success = execute 913
        accepted success |> ignore
        let! after = verified database

        Expect.equal
            after.Sequence
            (Sequence.successor checkpoint.Sequence)
            "A throwing observer cannot prevent the next real acceptance"
    }

let private readSetup (database: PostgresFixture.Database) =
    task {
        use! beforeConnection = database.OwnerSource.OpenConnectionAsync()

        let! beforeRows =
            DatabaseSession.rows beforeConnection "SELECT clock_timestamp()" [||] token

        let before = DatabaseSession.instant 0 beforeRows.Head

        let! observed =
            Transactions.read database.RuntimeSource token (fun connection observedAt ->
                task {
                    let! rows =
                        DatabaseSession.rows
                            connection
                            "SELECT current_setting('lock_timeout'),current_setting('statement_timeout'),current_setting('idle_in_transaction_session_timeout'),current_setting('transaction_isolation'),clock_timestamp()"
                            [||]
                            token

                    return observedAt, rows.Head
                })

        let instant, row =
            observed
            |> Result.defaultWith (fun error -> failtestf "Read setup failed: %A" error)

        Expect.equal
            (DatabaseSession.value<string> 0 row)
            "5s"
            "The original lock timeout is applied to this read transaction"

        Expect.equal
            (DatabaseSession.value<string> 1 row)
            "30s"
            "The original statement timeout is applied to this read transaction"

        Expect.equal
            (DatabaseSession.value<string> 2 row)
            "15s"
            "The original idle timeout is applied to this read transaction"

        Expect.equal
            (DatabaseSession.value<string> 3 row)
            "repeatable read"
            "The existing read snapshot isolation is preserved"

        let after = DatabaseSession.instant 4 row

        Expect.isTrue
            (before <= instant && instant <= after)
            "The supplied observation is an actual server-clock sample within the read setup"
    }

let private globalFactsIntegrity (database: PostgresFixture.Database) =
    task {
        let! owner = caseWorker database.Fixture

        let unknown =
            PrincipalKey.human "https://issuer.example/" "unknown-read-facts"
            |> Result.defaultWith (string >> failtest)

        let services = [ owner; core database.Fixture unknown ]

        let check label =
            task {
                for actor in services do
                    let! context = Service.context actor () token

                    Expect.equal
                        context
                        (Outcome.Failed CoreFault.StoreIntegrityError)
                        (label + ": context checks the operational owner even without an actor")

                    let! page =
                        Service.list
                            actor
                            (Inputs.caseListPage { AfterCursor = None; Limit = 1 })
                            token

                    Expect.equal
                        page
                        (Outcome.Failed CoreFault.StoreIntegrityError)
                        (label + ": list checks the operational owner before missing-actor refusal")
            }

        let! initial = Service.context owner () token

        match initial with
        | Outcome.Succeeded {
                                Requester = RequesterState.Active context
                            } -> Expect.isFalse context.Maintenance.Active "Positive ordinary mode"
        | other -> failtestf "Expected context: %A" other

        do! sql database "DELETE FROM claimcore.register_maintenance" [||]
        do! check "Missing operational row"

        do!
            sql
                database
                "INSERT INTO claimcore.register_maintenance(singleton,active,generation) VALUES(true,false,0)"
                [||]

        // This owned fixture deliberately crosses schema checks to exercise the independent read decoder.
        do!
            sql
                database
                "ALTER TABLE claimcore.register_maintenance DROP CONSTRAINT register_maintenance_check; UPDATE claimcore.register_maintenance SET active=true,generation=0"
                [||]

        do! check "Invalid operational state"

        do!
            sql
                database
                "UPDATE claimcore.register_maintenance SET active=false; ALTER TABLE claimcore.register_maintenance ADD CONSTRAINT register_maintenance_check CHECK (NOT active OR generation > 0)"
                [||]

        do!
            sql
                database
                "ALTER TABLE claimcore.register_maintenance DROP CONSTRAINT register_maintenance_singleton_check; INSERT INTO claimcore.register_maintenance(singleton,active,generation) VALUES(false,false,0)"
                [||]

        do! check "Multiple operational owners"

        do!
            sql
                database
                "DELETE FROM claimcore.register_maintenance WHERE NOT singleton; ALTER TABLE claimcore.register_maintenance ADD CONSTRAINT register_maintenance_singleton_check CHECK (singleton)"
                [||]

        let! repaired = Service.context owner () token

        match repaired with
        | Outcome.Succeeded {
                                Requester = RequesterState.Active context
                            } ->
            Expect.isFalse
                context.Maintenance.Active
                "Restored constraints and single valid owner remain readable"
        | other -> failtestf "Expected repaired context: %A" other

        let! absent = Service.context (core database.Fixture unknown) () token

        match absent with
        | Outcome.Succeeded {
                                Requester = RequesterState.NotRegistered requester
                            } ->
            Expect.equal
                requester.Issuer
                "https://issuer.example/"
                "Only the caller's issuer is disclosed"

            Expect.equal
                requester.Subject
                "unknown-read-facts"
                "Only the caller's identity is disclosed"
        | other -> failtestf "Expected the unregistered caller's own context state: %A" other

        let! protectedRead =
            Service.list
                (core database.Fixture unknown)
                (Inputs.caseListPage { AfterCursor = None; Limit = 1 })
                token

        Expect.equal
            protectedRead
            (Outcome.Rejected(Refusal.Access AccessRefusal.ResourceUnavailable))
            "Protected case reads remain non-disclosing"
    }

[<Tests>]
let tests =
    testSequenced
    <| testList
        "read setup and storage diagnostics"
        [
            testCaseAsync
                "global read facts retain operational integrity with known and absent actors"
                (run globalFactsIntegrity)
            testCaseAsync
                "transaction-local read settings and observation share one setup"
                (run readSetup)
            testCaseAsync
                "safe diagnostics preserve real pre-work read append projection and commit failures"
                (run controls)
        ]
