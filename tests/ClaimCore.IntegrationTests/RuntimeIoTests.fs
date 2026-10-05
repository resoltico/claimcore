module ClaimCore.IntegrationTests.RuntimeIoTests

open System
open System.Collections.Concurrent
open System.Diagnostics.Metrics
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private witnessOwner (writer: string) =
    let target = NpgsqlConnectionStringBuilder(writer)
    let owner = NpgsqlConnectionStringBuilder(witnessOwnerConnection ())
    owner.Database <- target.Database
    owner.ConnectionString

let private journalCount (owner: NpgsqlConnection) =
    use command =
        new NpgsqlCommand("SELECT count(*) FROM claimcore_witness.journal", owner)

    command.ExecuteScalar() :?> int64

let private cancelledAdmission _ _ writer (witness: WitnessProtocol) =
    use owner = new NpgsqlConnection(witnessOwner writer)
    owner.Open()
    let before = journalCount owner
    use transaction = owner.BeginTransaction()

    use barrier =
        new NpgsqlCommand(
            "LOCK TABLE claimcore_witness.installation IN ACCESS EXCLUSIVE MODE",
            owner,
            transaction
        )

    barrier.ExecuteNonQuery() |> ignore
    use cancellation = new CancellationTokenSource()

    let created =
        TaskCompletionSource<Task<unit>>(TaskCreationOptions.RunContinuationsAsynchronously)

    let dispatch =
        Task.Run(fun () -> created.SetResult(witness.Admit(cancellation.Token)))

    let mutable returned = false
    let mutable completed = false
    let mutable cancelled = false

    try
        returned <- SpinWait.SpinUntil((fun () -> created.Task.IsCompleted), 2000)
        cancellation.Cancel()

        if returned then
            let work = created.Task.Result
            completed <- SpinWait.SpinUntil((fun () -> work.IsCompleted), 2000)

            if completed then
                try
                    work.GetAwaiter().GetResult()
                with :? OperationCanceledException ->
                    cancelled <- true
    finally
        transaction.Rollback()
        dispatch.GetAwaiter().GetResult()

        try
            created.Task.Result.GetAwaiter().GetResult()
        with :? OperationCanceledException ->
            ()

    Expect.isTrue returned "Admission returns a task while database authority is blocked."
    Expect.isTrue completed "Cancellation completes while the database lock remains held."
    Expect.isTrue cancelled "Pre-dispatch cancellation retains its distinct meaning."
    Expect.equal (journalCount owner) before "Cancellation appended no authority evidence."

let private observedProtocol writer (original: WitnessProtocol) beforeSettlement observer =
    let store = witnessStore writer original.Identity
    let keyId, _ = store.ReadKeyCheck(CancellationToken.None).GetAwaiter().GetResult()
    let material = witnessKey ()

    try
        let custody = new KeyRing(keyId, [ keyId, material ]) :> IKeyCustody
        new WitnessProtocol(store, custody, original.Identity, beforeSettlement, observer)
    finally
        CryptographicOperations.ZeroMemory(material)

let private lateCancellation _ _ writer (original: WitnessProtocol) =
    use cancellation = new CancellationTokenSource()
    use witness = observedProtocol writer original cancellation.Cancel (fun _ _ -> ())
    let operation = Guid.NewGuid()

    let intent =
        witness
            .BeginAuthority(operation, [| 17uy; 29uy |], None, cancellation.Token)
            .GetAwaiter()
            .GetResult()

    let settled = witness.SettleAuthority(operation, intent).GetAwaiter().GetResult()
    Expect.isTrue cancellation.IsCancellationRequested "Cancellation occurred during settlement."

    witness
        .RequireSettled(operation, SettledAuthority, CancellationToken.None)
        .GetAwaiter()
        .GetResult()

    let replay = witness.SettleAuthority(operation, intent).GetAwaiter().GetResult()
    Expect.equal replay.Sequence settled.Sequence "Late cancellation retains the original sequence."

    Expect.sequenceEqual
        replay.EntryHash
        settled.EntryHash
        "Retry retains the exact settlement hash."

let private observerFailure _ _ writer (original: WitnessProtocol) =
    let events = ConcurrentQueue<WitnessFailureStage * WitnessFailureCause>()

    let observer stage cause =
        events.Enqueue(stage, cause)
        invalidOp "Synthetic observer failure"

    use witness =
        observedProtocol
            writer
            original
            (fun () -> raise (TimeoutException("Synthetic provider detail")))
            observer

    let operation = Guid.NewGuid()

    let intent =
        witness
            .BeginAuthority(operation, [| 41uy; 53uy |], None, CancellationToken.None)
            .GetAwaiter()
            .GetResult()

    Expect.throwsT<ClaimCore.Postgres.WitnessPending>
        (fun () -> witness.SettleAuthority(operation, intent).GetAwaiter().GetResult() |> ignore)
        "Observer failure cannot turn uncertain settlement into success or a different exception."

    let retained =
        witness.EvidenceStore
            .TryReadEvidence(operation, Intent, CancellationToken.None)
            .GetAwaiter()
            .GetResult()

    Expect.equal
        (retained |> Option.map _.Ticket.Sequence)
        (Some intent.Ticket.Sequence)
        "Exact intent survives observer failure."

    Expect.contains
        (events.ToArray())
        (WitnessFailureStage.Settlement, WitnessFailureCause.Transport)
        "Fixed cause and stage remain available."

let private quarantineSignals () =
    let records = ConcurrentQueue<string * string list>()
    use listener = new MeterListener()

    listener.InstrumentPublished <-
        Action<Instrument, MeterListener>(fun instrument active ->
            if instrument.Meter.Name = "ClaimCore.Runtime" then
                active.EnableMeasurementEvents(instrument))

    listener.SetMeasurementEventCallback<int64>(
        MeasurementCallback<int64>(fun instrument _ tags _ ->
            let values = ResizeArray<string>()

            for tag in tags do
                values.Add(tag.Key + "=" + string tag.Value)

            records.Enqueue(instrument.Name, Seq.toList values))
    )

    listener.Start()

    use cadence =
        new RuntimeAuditCadence(
            (fun _ -> Task.FromException(InvalidOperationException("Synthetic provider detail"))),
            TimeSpan.FromMilliseconds 10.
        )

    Expect.isTrue (cadence.Completion.Wait(2000)) "Failed audit completes its worker."

    for _ in 1..2 do
        Expect.throwsT<InvalidOperationException>
            cadence.RequireHealthy
            "Quarantine remains closed."

    let observed =
        records.ToArray()
        |> Array.filter (fun (name, _) -> name = "claimcore.audit.quarantines")

    Expect.equal observed.Length 1 "The sticky quarantine emits once."
    Expect.equal (snd observed[0]) [ "reason=failed" ] "Only the fixed safe reason is recorded."

let tests =
    testList
        "runtime I/O and diagnostics"
        [
            testCase "[CC-WIT-001] blocked witness admission cancels before dispatch" (fun _ ->
                withAuthorityRuntimeDatabase cancelledAdmission)
            testCase "[CC-WIT-001] late cancellation preserves exact settlement readback" (fun _ ->
                withAuthorityRuntimeDatabase lateCancellation)
            testCase
                "[CC-WIT-001] throwing observer preserves uncertain settlement and exact intent"
                (fun _ -> withAuthorityRuntimeDatabase observerFailure)
            testCase "[CC-AUDIT-001] failed audit emits one fixed quarantine signal" (fun _ ->
                quarantineSignals ())
        ]
    |> testSequenced
