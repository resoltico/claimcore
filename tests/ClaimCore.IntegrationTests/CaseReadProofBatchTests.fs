module ClaimCore.IntegrationTests.CaseReadProofBatchTests

open System
open System.Diagnostics
open System.Threading
open Expecto
open Npgsql
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.TestSupport
open ClaimCore.IntegrationTests.Fixtures

let private receipts count =
    use database = store ()
    let service = database :> IClaimStore

    [
        for _ in 1..count ->
            let request =
                { newRequest () with
                    CaseReference = "proof-" + Guid.NewGuid().ToString("N")
                }

            CommandExecution.executeAsync service clock request |> await |> accepted
    ]

let private commandListener increment =
    let listener = new ActivityListener()

    listener.ShouldListenTo <-
        Func<ActivitySource, bool>(fun source ->
            source.Name.StartsWith("Npgsql", StringComparison.Ordinal))

    listener.Sample <- SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllData)

    listener.ActivityStopped <-
        Action<Activity>(fun activity ->
            if activity.Kind = ActivityKind.Client then
                increment ())

    ActivitySource.AddActivityListener listener
    listener

let private proof count =
    testCase
        ($"[CC-AUTH-001] bounded page proves {count} exact independently witnessed subjects")
        (fun () ->
            let items = receipts count
            let witness = witnessProtocol ()
            use source = NpgsqlDataSource.Create(appConnection ())
            use connection = source.OpenConnection()
            use transaction = connection.BeginTransaction()

            use primary =
                new NpgsqlCommand(
                    "SELECT revision FROM claimcore.authority_tip WHERE singleton FOR SHARE",
                    connection,
                    transaction
                )

            primary.ExecuteScalar() |> ignore
            use fence = witness.AcquireReadFence(1L, CancellationToken.None) |> await
            let mutable commands = 0

            use listener =
                commandListener (fun () -> Interlocked.Increment(&commands) |> ignore)

            let memory = GC.GetTotalAllocatedBytes(true)
            let started = Stopwatch.StartNew()

            let result =
                CaseReadEvidence.page
                    witness
                    connection
                    transaction
                    (items |> List.map _.Case)
                    CancellationToken.None
                |> await

            let allocation = GC.GetTotalAllocatedBytes(true) - memory
            Expect.isNone result "Every subject retains its full independent proof."
            let chunks = (count + 3) / 4

            Expect.isGreaterThan
                commands
                0
                "Actual database instrumentation must observe commands."

            Expect.isLessThanOrEqual
                commands
                (chunks * 8)
                "Fixed acquisition commands per chunk include witness admission."

            printfn
                "PROOF_MEASUREMENT subjects=%d commands=%d elapsedMs=%.3f allocationBytes=%d"
                count
                commands
                started.Elapsed.TotalMilliseconds
                allocation)

let private unavailableSettlement () =
    let item = receipts 1 |> List.head
    let witness = witnessProtocol ()
    use source = NpgsqlDataSource.Create(appConnection ())
    use connection = source.OpenConnection()
    use primary = connection.BeginTransaction()

    let evidence =
        witness.EvidenceStore.ReadEvidenceBatch(
            [ item.OperationId, ClaimCore.Witness.SettledAccepted ],
            CancellationToken.None
        )
        |> await

    evidence.Remove((item.OperationId, ClaimCore.Witness.SettledAccepted)) |> ignore

    Expect.throws
        (fun () ->
            CaseReadProof.pair evidence item.OperationId ClaimCore.Witness.SettledAccepted
            |> ignore)
        "One missing exact settlement fails closed."

    let rows =
        CaseReadProof.loadOperations
            connection
            primary
            [| item.OperationId |]
            CancellationToken.None
        |> await

    let retained =
        witness.EvidenceStore.ReadEvidenceBatch(
            [ item.OperationId, ClaimCore.Witness.SettledAccepted ],
            CancellationToken.None
        )
        |> await

    let intent, settlement =
        CaseReadProof.pair retained item.OperationId ClaimCore.Witness.SettledAccepted

    let swapped =
        { settlement with
            Ticket =
                { settlement.Ticket with
                    SubjectCaseId = Some(Guid.NewGuid())
                }
        }

    Expect.throws
        (fun () ->
            CaseReadProof.verifyAccepted
                witness
                item.OperationId
                rows[item.OperationId]
                (intent, swapped))
        "Cross-case witness reuse fails closed."

let tests =
    testList
        "set-oriented read proof"
        ([ 1; 8; 9; 50 ]
         |> List.map proof
         |> fun tests ->
             tests
             @ [
                 testCase
                     "[CC-AUTH-001] missing or swapped loaded settlement refuses the whole proof"
                     unavailableSettlement
             ])
