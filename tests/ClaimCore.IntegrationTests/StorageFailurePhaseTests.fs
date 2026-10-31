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
            Transactions.read database.RuntimeSource cancelled.Token (fun _ -> Task.FromResult 0)

        Expect.equal
            unopened
            (Error StoreFailure.Cancelled)
            "Pre-work cancellation is definite and does not invoke work"

        let! read =
            Transactions.read database.RuntimeSource token (fun connection ->
                DatabaseSession.rows connection ("SELECT '" + sensitive + "'::integer") [||] token)

        Expect.equal
            (read |> Result.map ignore)
            (Error StoreFailure.Unavailable)
            "Real provider read failure is mapped before exception details reach callers"

        let! internalFailure =
            Transactions.read database.RuntimeSource token (fun _ ->
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

[<Tests>]
let tests =
    testSequenced
    <| testCaseAsync
        "safe diagnostics preserve real pre-work read append projection and commit failures"
        (run controls)
