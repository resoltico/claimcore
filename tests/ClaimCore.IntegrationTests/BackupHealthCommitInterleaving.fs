module internal ClaimCore.IntegrationTests.BackupHealthCommitInterleaving

open System
open System.Data
open System.IO
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open NpgsqlTypes
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.ManagedCopyInventoryFixture
open ClaimCore.IntegrationTests.ManagedCopyPhysicalProcessDocuments
open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerSetup
open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerRefusals

let private copyState (connection: NpgsqlConnection) transaction copyId =
    use command =
        match transaction with
        | Some current ->
            new NpgsqlCommand(
                "SELECT state FROM claimcore.managed_copies WHERE copy_id=@copy",
                connection,
                current
            )
        | None ->
            new NpgsqlCommand(
                "SELECT state FROM claimcore.managed_copies WHERE copy_id=@copy",
                connection
            )

    Sql.uuid command "copy" copyId

    match command.ExecuteScalar() with
    | :? string as state -> state
    | _ -> failtest "Verified physical copy row is absent."

let private eventHash (connection: NpgsqlConnection) copyId =
    use command =
        new NpgsqlCommand(
            "SELECT event_hash FROM claimcore.managed_copies WHERE copy_id=@copy",
            connection
        )

    Sql.uuid command "copy" copyId

    match command.ExecuteScalar() with
    | :? (byte array) as value -> value
    | _ -> failtest "Verified physical copy hash is absent."

let private waiting (observer: NpgsqlConnection) pid =
    use command =
        new NpgsqlCommand(
            "SELECT wait_event_type='Lock' FROM pg_stat_activity WHERE pid=@pid",
            observer
        )

    command.Parameters.AddWithValue("pid", NpgsqlDbType.Integer, pid) |> ignore

    match command.ExecuteScalar() with
    | :? bool as blocked -> blocked
    | _ -> false

let private waitForLock observer pid =
    Expect.isTrue
        (SpinWait.SpinUntil((fun () -> waiting observer pid), 10000))
        "The isolated backend reached its primary authority lock wait."

let private unknownTransition
    (transitionOwner: NpgsqlConnection)
    (witness: WitnessProtocol)
    copyId
    registration
    proof
    (copyKey: Key)
    (algorithm: SignatureAlgorithm)
    =
    let previous = eventHash transitionOwner copyId

    let parsed =
        ManagedCopyPhysicalProofCodec.parse proof
        |> Option.defaultWith (fun () -> failtest "Verified physical proof cannot be parsed.")

    let eventId = Guid.NewGuid()

    let transition =
        transitionFromRegister
            registration
            eventId
            3L
            "UNKNOWN"
            "UNKNOWN"
            previous
            (witness.Snapshot())
        |> fun source ->
            changed
                source
                [
                    "verificationProofSha256", element (digest proof)
                    "lastVerifiedAt", element (stamp parsed.CheckedAt)
                ]

    eventId, transition, algorithm.Sign(copyKey, transition)

let unknownFromVerified
    owner
    (witness: WitnessProtocol)
    (copy: VerifiedPhysicalCopy)
    (copyKey: Key)
    (algorithm: SignatureAlgorithm)
    =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use registration =
        new NpgsqlCommand(
            "SELECT canonical_attestation FROM claimcore.managed_copy_events "
            + "WHERE copy_id=@copy AND revision=1",
            connection
        )

    Sql.uuid registration "copy" copy.CopyId

    let original =
        match registration.ExecuteScalar() with
        | :? (byte array) as value -> value
        | _ -> invalidOp "Original physical copy attestation is absent."

    let proof = File.ReadAllBytes(copy.ProofPath)

    let eventId, canonical, signature =
        unknownTransition connection witness copy.CopyId original proof copyKey algorithm

    match
        ManagedCopyTransitionAdministration.transition connection witness canonical signature
        |> await
    with
    | AuthorityWriteOutcome.Applied(id, 3L) when id = eventId -> ()
    | _ -> failtest "Genuine retained copy did not become witnessed UNKNOWN."

let private actorAttempt
    owner
    (witness: WitnessProtocol)
    copyId
    (actorPid: TaskCompletionSource<int>)
    =
    Task.Run(fun () ->
        use actor = new NpgsqlConnection(owner)
        actor.Open()
        actorPid.SetResult(actor.ProcessID)
        use transaction = actor.BeginTransaction(IsolationLevel.ReadCommitted)

        let guard =
            { new ICaseMutationCommitHealth with
                member _.VerifyLocked(connection, current) =
                    if copyState connection (Some current) copyId <> "RETAINED" then
                        invalidOp "Signed retained-copy health became stale."
            }

        use _scope = CaseMutationCommitHealth.enter guard

        ActorGrantRead.lockRevision actor transaction true CancellationToken.None
        |> await
        |> ignore

        witness.BeginAuthority(Guid.NewGuid(), [| 1uy |], None) |> ignore)

let private settledRace
    owner
    (witness: WitnessProtocol)
    copyId
    (observer: NpgsqlConnection)
    (transitionOwner: NpgsqlConnection)
    (held: NpgsqlTransaction)
    eventId
    before
    (ownerTask: Task<AuthorityWriteOutcome>)
    (actorTask: Task option ref)
    =
    waitForLock observer transitionOwner.ProcessID

    let actorPid =
        TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)

    let actor = actorAttempt owner witness copyId actorPid
    actorTask.Value <- Some actor

    Expect.isTrue
        (actorPid.Task.Wait(TimeSpan.FromSeconds 10.))
        "The isolated actor backend opened before the lock deadline."

    waitForLock observer (actorPid.Task.GetAwaiter().GetResult())
    held.Rollback()

    match ownerTask.GetAwaiter().GetResult() with
    | AuthorityWriteOutcome.Applied(id, 3L) when id = eventId -> ()
    | _ -> failtest "The genuine retained-to-unknown transition did not settle."

    Expect.throwsT<InvalidOperationException>
        (fun () -> actor.GetAwaiter().GetResult())
        "The actor refuses before witness INTENT after health changed under authority lock."

    Expect.equal
        (copyState transitionOwner None copyId)
        "UNKNOWN"
        "The owner transition is visible before the actor health recheck."

    Expect.equal
        (witness.Snapshot().TipSequence)
        (before + 2L)
        "Only the owner copy transition appended witness evidence."

let private finishRace
    (held: NpgsqlTransaction)
    (ownerTask: Task<AuthorityWriteOutcome>)
    (actorTask: Task option ref)
    =
    try
        held.Rollback()
    with _ ->
        ()

    try
        (ownerTask :> Task).Wait(TimeSpan.FromSeconds 10.) |> ignore
    with _ ->
        ()

    actorTask.Value
    |> Option.iter (fun task ->
        try
            task.Wait(TimeSpan.FromSeconds 10.) |> ignore
        with _ ->
            ())

let verify
    owner
    (witness: WitnessProtocol)
    copyId
    registration
    proof
    (copyKey: Key)
    (algorithm: SignatureAlgorithm)
    =
    use blocker = new NpgsqlConnection(owner)
    use transitionOwner = new NpgsqlConnection(owner)
    use observer = new NpgsqlConnection(owner)
    blocker.Open()
    transitionOwner.Open()
    observer.Open()

    let eventId, transition, signature =
        unknownTransition transitionOwner witness copyId registration proof copyKey algorithm

    Expect.equal
        (copyState transitionOwner None copyId)
        "RETAINED"
        "The concurrency oracle begins from a genuinely verified retained copy."

    let before = witness.Snapshot().TipSequence
    use held = blocker.BeginTransaction(IsolationLevel.ReadCommitted)

    ActorGrantRead.lockRevision blocker held true CancellationToken.None
    |> await
    |> ignore

    let ownerTask: Task<AuthorityWriteOutcome> =
        Task.Run(fun () ->
            ManagedCopyTransitionAdministration.transition
                transitionOwner
                witness
                transition
                signature
            |> await)

    let actorTask: Task option ref = ref None

    try
        settledRace
            owner
            witness
            copyId
            observer
            transitionOwner
            held
            eventId
            before
            ownerTask
            actorTask
    finally
        finishRace held ownerTask actorTask
