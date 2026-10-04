module ClaimCore.IntegrationTests.BackupHealthRuntimeActorRaceTests

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyPhysicalOwnerSetup
open ClaimCore.IntegrationTests.ManagedCopyPhysicalProcessTests
open ClaimCore.IntegrationTests.BackupHealthSourceDocuments
open ClaimCore.IntegrationTests.BackupHealthRuntimeActorRaceFacts
open ClaimCore.IntegrationTests.BackupHealthRuntimeActorRaceDocuments
open ClaimCore.IntegrationTests.BackupHealthCommitInterleaving

let private admittance
    app
    (witness: WitnessProtocol)
    profile
    policy
    canonical
    signature
    (checkedAt: TaskCompletionSource<unit>)
    (release: TaskCompletionSource<unit>)
    onLocked
    onRefused
    =
    let verifyCurrent () =
        use connection = new NpgsqlConnection(app)
        connection.Open()
        BackupHealthRuntimeAdmission.verify connection witness profile policy canonical signature

    let guard =
        { new ICaseMutationCommitHealth with
            member _.VerifyLocked(connection, transaction) =
                onLocked ()

                try
                    BackupHealthRuntimeAdmission.verifyLocked
                        connection
                        transaction
                        witness
                        profile
                        policy
                        canonical
                        signature
                with :? InvalidOperationException ->
                    onRefused ()
                    reraise ()
        }

    new RuntimeAdmission(
        { new IDisposable with
            member _.Dispose() = ()
        },
        TimeSpan.FromSeconds 10.,
        (fun () -> witness.AdmitReadOnly()),
        (fun () -> witness.AcquireReadFence(witness.Snapshot().WriterGeneration)),
        {
            RequireCaseMutation =
                (fun () ->
                    verifyCurrent ()
                    checkedAt.TrySetResult() |> ignore

                    if not (release.Task.Wait(TimeSpan.FromSeconds 30.)) then
                        invalidOp "Synthetic mutation race was not released.")
            RequireCaseRead = (fun () -> ())
            RequireAuthoritySetup = (fun () -> ())
            RequireAuthorityRead = (fun () -> ())
            CommitHealth = guard
            CommitHealthRequired = true
        }
    )

let private assertRefusal
    owner
    (witness: WitnessProtocol)
    (request: CommandRequest)
    before
    (entered: unit -> int)
    (refused: unit -> int)
    (execution: Task<SubmissionOutcome>)
    =
    let outcome =
        try
            Some(execution.GetAwaiter().GetResult())
        with :? InvalidOperationException ->
            None

    Expect.isTrue
        (entered () > 0)
        "The actor reached the real signed commit-side verifier under authority lock."

    Expect.isTrue (refused () > 0) "The signed commit-side verifier refused the lost retained copy."

    match outcome with
    | Some(SubmissionOutcome.Completed(_, _, DefiniteExecution.Accepted _, _)) ->
        failtest "Stale health cannot accept a claimant mutation."
    | _ -> ()

    Expect.equal
        (acceptedHistory owner request.OperationId)
        0L
        "No accepted primary history follows stale health."

    Expect.isNone
        (witness.EvidenceStore.TryReadEvidence(request.OperationId, Intent))
        "No actor witness INTENT follows stale health."

    Expect.equal
        (witness.Snapshot().TipSequence)
        (before + 2L)
        "Only the owner copy transition appended evidence."

let private unknownPrimaryBase
    owner
    (witness: WitnessProtocol)
    (verified: VerifiedPhysicalCopy list)
    (copyKey: Key)
    (copyAlgorithm: SignatureAlgorithm)
    =
    let copy =
        verified
        |> List.find (fun item -> item.Cluster = "PRIMARY" && item.Kind = "BASE")

    unknownFromVerified owner witness copy copyKey copyAlgorithm

let private blockedExecution
    owner
    (witness: WitnessProtocol)
    (runtime: Runtime)
    principal
    verified
    copyKey
    copyAlgorithm
    admission
    (checkedAt: TaskCompletionSource<unit>)
    (release: TaskCompletionSource<unit>)
    entered
    refused
    =
    let actor = runtime.ForActorWithAdmission(principal, admission)
    let request = newRequest ()
    let before = witness.Snapshot().TipSequence

    let execution =
        Task.Run(fun () -> actor.Execute(request, CancellationToken.None) |> await)

    try
        let ready = Task.WhenAny(checkedAt.Task :> Task, execution :> Task)

        Expect.isTrue
            (ready.Wait(TimeSpan.FromSeconds 20.))
            "Health checkpoint or execution completed."

        if not checkedAt.Task.IsCompleted then
            execution.GetAwaiter().GetResult() |> ignore
            failtest "Actor execution ended before the signed health checkpoint."

        unknownPrimaryBase owner witness verified copyKey copyAlgorithm
        release.TrySetResult() |> ignore
        assertRefusal owner witness request before entered refused execution
    finally
        release.TrySetResult() |> ignore

        try
            (execution :> Task).Wait(TimeSpan.FromSeconds 10.) |> ignore
        with _ ->
            ()

let private race
    owner
    app
    (witness: WitnessProtocol)
    (runtime: Runtime)
    (principal: PrincipalKey)
    (verified: VerifiedPhysicalCopy list)
    (copyKey: Key)
    (copyAlgorithm: SignatureAlgorithm)
    (profile: ReviewedDeploymentProfile)
    (policy: byte array)
    (canonical: byte array)
    (signature: byte array)
    =
    let checkedAt =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let release =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

    let mutable commitHealthEntered = 0
    let mutable commitHealthRefused = 0

    use admission =
        admittance
            app
            witness
            profile
            policy
            canonical
            signature
            checkedAt
            release
            (fun () -> Interlocked.Increment(&commitHealthEntered) |> ignore)
            (fun () -> Interlocked.Increment(&commitHealthRefused) |> ignore)

    blockedExecution
        owner
        witness
        runtime
        principal
        verified
        copyKey
        copyAlgorithm
        admission
        checkedAt
        release
        (fun () -> Volatile.Read(&commitHealthEntered))
        (fun () -> Volatile.Read(&commitHealthRefused))

let private run owner app writer (witness: WitnessProtocol) =
    withVerifiedRegisteredCopiesUsingSigner
        (fun capture registered verified copyKey copyAlgorithm ->
            use runtime = openRuntime app writer
            let principal = human "physical-restore-owner"
            let holder = human "physical-health-checkpoint-holder"
            grantCustodian runtime principal holder
            use connection = new NpgsqlConnection(owner)
            connection.Open()

            let key, algorithm, keyId, _ =
                registeredSigner
                    runtime
                    principal
                    holder
                    CopySignerPurpose.Checkpoint
                    witness
                    connection

            use key = key
            let documents = create ()

            try
                let claims = build connection witness capture registered verified keyId
                let canonical = certificate claims

                Expect.isSome
                    (BackupHealthCertificate.parse canonical claims.CheckedAt)
                    "Synthetic health claims are canonical and temporally consistent at their observation."

                let signature = algorithm.Sign(key, canonical)

                let profile = reviewedProfile documents.Loaded.PolicyBytes

                race
                    owner
                    app
                    witness
                    runtime
                    principal
                    verified
                    copyKey
                    copyAlgorithm
                    profile
                    documents.Loaded.PolicyBytes
                    canonical
                    signature
            finally
                dispose documents)
        owner
        app
        writer
        witness

let tests =
    testList
        "runtime signed backup health"
        [
            testCase
                "[CC-BACKUP-001] signed current health cannot authorize actor Execute after retained-copy loss"
                (fun _ -> withAuthorityRuntimeDatabase run)
        ]
