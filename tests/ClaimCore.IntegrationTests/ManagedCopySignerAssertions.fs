module internal ClaimCore.IntegrationTests.ManagedCopySignerAssertions

open System.Threading
open System
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Application
open ClaimCore.Hosting
open ClaimCore.Postgres
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.ManagedCopySignerTestSupport

let verifyApprovals (connection: NpgsqlConnection) (witness: WitnessProtocol) ids =
    use transaction = connection.BeginTransaction()

    for approvalId in ids do
        let evidence =
            ManagedCopySignerApprovalRead.load connection transaction approvalId
            |> await
            |> Option.defaultWith (fun () -> failtest "Witnessed approval row is absent.")

        Expect.isTrue evidence.CurrentActorAndGrant "Approval grant remains current."
        Expect.isFalse evidence.Used "Approval has not been consumed."

        Expect.isTrue
            (ManagedCopySignerApprovalRead.canonicalMatches evidence)
            "Approval row binds its exact canonical candidate."

        (witness
            .VerifyAuthorityEvidence(
                approvalId,
                evidence.WitnessSequence,
                evidence.WitnessEpoch,
                evidence.WitnessEntryHash,
                evidence.CandidateSha256,
                CancellationToken.None
            )
            .GetAwaiter()
            .GetResult())

    transaction.Rollback()

let requireFresh (connection: NpgsqlConnection) keyId eventId =
    use transaction = connection.BeginTransaction()

    Expect.isNone
        (ManagedCopySignerWrite.state connection transaction keyId |> await)
        "Fresh signer has no roster row."

    Expect.isNone
        (ManagedCopySignerWrite.event connection transaction eventId |> await)
        "Fresh signer event is absent."

    use clock = new NpgsqlCommand("SELECT clock_timestamp()", connection, transaction)

    match clock.ExecuteScalar() with
    | :? DateTimeOffset -> ()
    | :? DateTime as timestamp when timestamp.Kind = DateTimeKind.Utc -> ()
    | _ -> failtest "PostgreSQL signer approval clock type is unexpected."

    transaction.Rollback()

let registerVerified
    owner
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    eventId
    keyId
    raw
    ownerApproval
    custodianApproval
    =
    let first =
        ManagedCopySignerAdministration.register
            connection
            witness
            eventId
            keyId
            CopySignerPurpose.CopyAttestor
            raw
            ownerApproval
            custodianApproval
        |> await

    match first with
    | AuthorityWriteOutcome.Unconfirmed _ ->
        let signer, intents =
            diagnosticCounts owner (witnessOwnerConnection ()) keyId eventId

        failtestf "Signer confirmation unknown: primary rows %d; witness rows %d." signer intents
    | other -> appliedSigner eventId other

    let active, revision, storedKey = signerStatus owner keyId
    Expect.isTrue active "Registered signer is active."
    Expect.equal revision 1L "Registration writes one roster revision."
    Expect.sequenceEqual storedKey raw "Raw public verification key is retained."

    ManagedCopySignerAdministration.register
        connection
        witness
        eventId
        keyId
        CopySignerPurpose.CopyAttestor
        raw
        ownerApproval
        custodianApproval
    |> await
    |> appliedSigner eventId

let rejectChanged
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    (algorithm: SignatureAlgorithm)
    eventId
    keyId
    ownerApproval
    custodianApproval
    =
    use another = Key.Create(algorithm)
    let changedKey = another.PublicKey.Export(KeyBlobFormat.RawPublicKey)

    Expect.equal
        (ManagedCopySignerAdministration.register
            connection
            witness
            eventId
            keyId
            CopySignerPurpose.CopyAttestor
            changedKey
            ownerApproval
            custodianApproval
         |> await)
        AuthorityWriteOutcome.Refused
        "Changed key under one event ID cannot replay registration."

let retireVerified
    owner
    (runtime: Runtime)
    ownerPrincipal
    custodian
    (connection: NpgsqlConnection)
    (witness: WitnessProtocol)
    keyId
    digest
    raw
    =
    let retireOwner, retireCustodian =
        approvePair
            runtime
            ownerPrincipal
            custodian
            keyId
            digest
            CopySignerAction.Retire
            CopySignerPurpose.CopyAttestor

    let retireEvent = Guid.NewGuid()

    ManagedCopySignerAdministration.retire
        connection
        witness
        retireEvent
        keyId
        CopySignerPurpose.CopyAttestor
        retireOwner
        retireCustodian
    |> await
    |> appliedSigner retireEvent

    let active, revision, retainedKey = signerStatus owner keyId
    Expect.isFalse active "Retirement ends future signer authority."
    Expect.equal revision 2L "Retirement adds one roster revision."
    Expect.sequenceEqual retainedKey raw "Old verification key stays for historical audit."

let verifyRoster
    owner
    (runtime: Runtime)
    ownerPrincipal
    custodian
    (witness: WitnessProtocol)
    algorithm
    keyId
    digest
    raw
    ownerApproval
    custodianApproval
    =
    let eventId = Guid.NewGuid()
    use connection = new NpgsqlConnection(owner)
    connection.Open()
    OwnerConnection.requireIdentity connection
    SchemaBaseline.requireCurrent connection
    verifyApprovals connection witness [ ownerApproval; custodianApproval ]
    requireFresh connection keyId eventId
    registerVerified owner connection witness eventId keyId raw ownerApproval custodianApproval
    rejectChanged connection witness algorithm eventId keyId ownerApproval custodianApproval
    retireVerified owner runtime ownerPrincipal custodian connection witness keyId digest raw
