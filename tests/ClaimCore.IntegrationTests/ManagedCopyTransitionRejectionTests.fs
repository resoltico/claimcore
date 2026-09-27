module ClaimCore.IntegrationTests.ManagedCopyTransitionRejectionTests

open System
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.ActorGrantTestSupport
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport
open ClaimCore.IntegrationTests.ManagedCopyAttestationFixture
open ClaimCore.IntegrationTests.ManagedCopyAuditTestSupport
open ClaimCore.IntegrationTests.ManagedCopyIngestTests

let private refused =
    function
    | AuthorityWriteOutcome.Refused -> ()
    | _ -> failtest "Invalid managed-copy evidence must be refused before acceptance."

let private attempt
    (witness: WitnessProtocol)
    (algorithm: SignatureAlgorithm)
    (key: Key)
    (registration: byte array)
    kind
    state
    hash
    =
    let eventId = Guid.NewGuid()

    let canonical =
        transitionFromRegister registration eventId 2 kind state hash (witness.Snapshot())

    eventId, canonical, algorithm.Sign(key, canonical)

let private assertRejectedTransitions connection witness algorithm key registration previous =
    for kind, state, hash in
        [
            "DELETE_REQUEST", "DELETE_PENDING", previous
            "VERIFY", "RETAINED", previous
            "VERIFIED_DELETED", "VERIFIED_DELETED", previous
            "UNKNOWN", "UNKNOWN", Array.zeroCreate<byte> 32
        ] do
        let _, canonical, signature =
            attempt witness algorithm key registration kind state hash

        ManagedCopyTransitionAdministration.transition connection witness canonical signature
        |> await
        |> refused

let private assertExactUnknown connection witness algorithm key registration previous =
    let eventId, canonical, signature =
        attempt witness algorithm key registration "UNKNOWN" "UNKNOWN" previous

    for _ in 1..2 do
        match
            ManagedCopyTransitionAdministration.transition connection witness canonical signature
            |> await
        with
        | AuthorityWriteOutcome.Applied(id, 2L) when id = eventId -> ()
        | _ -> failtest "Exact witnessed custody transition did not replay."

let private assertTamperedProjection
    app
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    copyId
    =
    use source = RuntimeDataSource.create app
    use audit = RuntimeDatabase.openConnection source
    let summary = DataAudit.run audit witness Threading.CancellationToken.None |> await
    Expect.equal summary.OwnerManagedCopies 1L "Full owner-copy chain is audited."

    use altered =
        new NpgsqlCommand(
            "UPDATE claimcore.managed_copies SET event_hash=decode(repeat('f',64),'hex') "
            + "WHERE copy_id=@copy",
            connection
        )

    altered.Parameters.AddWithValue("copy", copyId) |> ignore
    Expect.equal (altered.ExecuteNonQuery()) 1 "Synthetic projection was altered."

    Expect.throwsT<IO.InvalidDataException>
        (fun _ -> DataAudit.run audit witness Threading.CancellationToken.None |> await |> ignore)
        "Full audit refuses a changed copy projection."


let private exercise owner app writer witness =
    let ownerPrincipal = human "copy-transition-owner"
    let custodian = human "copy-transition-custodian"
    provision owner witness ownerPrincipal |> ActorGrantTestSupport.applied
    use runtime = openRuntime app writer
    grantCustodian runtime ownerPrincipal custodian
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    let key, algorithm, keyId, _ =
        registeredSigner
            runtime
            ownerPrincipal
            custodian
            CopySignerPurpose.CopyAttestor
            witness
            connection

    use key = key
    let registrationEvent = Guid.NewGuid()
    let copyId = Guid.NewGuid()

    let registration =
        registerBase owner (witness.Snapshot()) keyId registrationEvent copyId

    let registrationSignature = algorithm.Sign(key, registration)

    ManagedCopyAdministration.ingest connection witness registration registrationSignature
    |> await
    |> acceptedCopy registrationEvent

    let previous =
        ManagedCopyEventHash.compute
            (Array.zeroCreate<byte> 32)
            registration
            (Some registrationSignature)

    assertRejectedTransitions connection witness algorithm key registration previous
    assertExactUnknown connection witness algorithm key registration previous
    assertTamperedProjection app connection witness copyId

let private custodyTransition =
    testCase
        "[CC-BACKUP-001] signed custody uncertainty replays and unproven deletion stays closed"
        (fun _ -> withAuthorityRuntimeDatabase exercise)


let tests = testList "managed-copy owner ingest" [ custodyTransition ]
