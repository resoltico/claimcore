module ClaimCore.IntegrationTests.ManagedCopyHoldTests

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Hosting
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.CaseLifecycleStoreFixture
open ClaimCore.IntegrationTests.CaseLifecycleStoreTests
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.ManagedCopyIngestTests
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ActorGrantTestSupport

let private caseId owner reference =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    use command =
        new NpgsqlCommand(
            "SELECT case_id FROM claimcore.cases WHERE case_reference=@reference",
            connection
        )

    Sql.text command "reference" reference
    command.ExecuteScalar() :?> Guid

let private registration
    owner
    (witness: WitnessProtocol)
    keyId
    (key: Key)
    (algorithm: SignatureAlgorithm)
    kind
    sourceCaseId
    retainUntil
    =
    let eventId = Guid.NewGuid()
    let copyId = Guid.NewGuid()
    let tip = witness.Snapshot()

    let raw =
        if kind = "BASE" then
            registerBase owner tip keyId eventId copyId
        else
            registerVariant owner tip keyId eventId copyId kind "none" sourceCaseId

    let canonical =
        ClaimCore.IntegrationTests.ManagedCopyInventoryFixture.changed
            raw
            [
                "retainUntil",
                ClaimCore.IntegrationTests.ManagedCopyInventoryFixture.element retainUntil
            ]

    canonical, algorithm.Sign(key, canonical), eventId

let private attemptDelete
    connection
    (witness: WitnessProtocol)
    (key: Key)
    (algorithm: SignatureAlgorithm)
    canonical
    signature
    =
    let previous =
        ManagedCopyEventHash.compute (Array.zeroCreate<byte> 32) canonical (Some signature)

    let eventId = Guid.NewGuid()

    let transition =
        transitionFromRegister
            canonical
            eventId
            2
            "DELETE_REQUEST"
            "DELETE_PENDING"
            previous
            (witness.Snapshot())

    let result =
        ManagedCopyTransitionAdministration.transition
            connection
            witness
            transition
            (algorithm.Sign(key, transition))
        |> await

    Expect.equal result AuthorityWriteOutcome.Refused "Held copy cannot enter deletion pending."

let private heldCopies owner _ (witness: WitnessProtocol) (runtime: Runtime) proposer first _ _ _ =
    let actor = runtime.ForActor proposer

    let request =
        openRequest (Guid.NewGuid()) ("COPY-HOLD-" + Guid.NewGuid().ToString("N"))

    executeAccepted actor request
    let linkedCaseId = caseId owner request.CaseReference
    let current = review actor request.CaseReference

    let hold =
        change
            (Guid.NewGuid())
            request.CaseReference
            current
            (LifecycleMutation.RecordHold(
                Guid.NewGuid(),
                "Synthetic copy retention hold",
                DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10.))
            ))

    match actor.Lifecycle.Apply(hold, CancellationToken.None) |> await with
    | LifecycleWriteOutcome.Applied _ -> ()
    | _ -> failtest "Synthetic hold was not witnessed."

    use connection = new NpgsqlConnection(owner)
    connection.Open()
    let algorithm = SignatureAlgorithm.Ed25519

    let key, _, keyId, _ =
        registeredSigner runtime proposer first CopySignerPurpose.CopyAttestor witness connection

    use key = key

    let retainUntil =
        DateTimeOffset.UtcNow.AddSeconds(4.).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")

    let copies =
        [
            registration owner witness keyId key algorithm "BASE" None retainUntil
            registration owner witness keyId key algorithm "EXPORT" (Some linkedCaseId) retainUntil
        ]

    for canonical, signature, eventId in copies do
        ManagedCopyAdministration.ingest connection witness canonical signature
        |> await
        |> acceptedCopy eventId

    Thread.Sleep(4500)
    let before = witness.Snapshot().TipSequence

    for canonical, signature, _ in copies do
        attemptDelete connection witness key algorithm canonical signature |> ignore

    Expect.equal
        (witness.Snapshot().TipSequence)
        before
        "Hold refusal creates no new witness intent."

let private postpruneCaseCopy
    owner
    dataSource
    (witness: WitnessProtocol)
    (runtime: Runtime)
    proposer
    first
    second
    ungranted
    writer
    =
    let holder = human "postprune-copy-holder"
    grantCustodian runtime proposer holder
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let key, algorithm, keyId, _ =
        registeredSigner runtime proposer holder CopySignerPurpose.CopyAttestor witness connection

    use key = key

    let beforeRequest _ input =
        let linkedCaseId = caseId owner input.CaseReference

        let canonical, signature, eventId =
            registration
                owner
                witness
                keyId
                key
                algorithm
                "EXPORT"
                (Some linkedCaseId)
                (DateTimeOffset.UtcNow.AddDays(30.).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))

        ManagedCopyAdministration.ingest connection witness canonical signature
        |> await
        |> acceptedCopy eventId

    CaseWitnessPayloadPruneTests.runWithBeforeRequest
        beforeRequest
        owner
        dataSource
        witness
        runtime
        proposer
        first
        second
        ungranted
        writer

let tests =
    testList
        "managed-copy hold"
        [
            testCase
                "[CC-BACKUP-001] active case hold blocks deletion intent for global and case-linked copies"
                (fun _ -> setup heldCopies)
            testCase
                "[CC-BACKUP-001] postprune full audit verifies sealed case-linked owner copy"
                (fun _ -> setup postpruneCaseCopy)
        ]
