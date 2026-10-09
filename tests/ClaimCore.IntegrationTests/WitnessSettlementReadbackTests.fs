module ClaimCore.IntegrationTests.WitnessSettlementReadbackTests

open System
open System.IO
open System.Security.Cryptography
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures
open ClaimCore.IntegrationTests.TechnicalWitnessTestSupport

let private ct = CancellationToken.None

let private custody (inner: IKeyCustody) target unavailable observed =
    { new IKeyCustody with
        member _.ActiveKeyId = inner.ActiveKeyId
        member _.HasKey id = inner.HasKey id
        member _.Encrypt(id, aad, bytes) = inner.Encrypt(id, aad, bytes)

        member _.Decrypt(id, aad, bytes) =
            if aad = target then
                observed ()

                if unavailable then
                    raise (IOException("synthetic exact verification unavailable"))

            inner.Decrypt(id, aad, bytes)

        member _.Dispose() = inner.Dispose()
    }

let private settlePersistedAcceptance
    owner
    (healthy: WitnessProtocol)
    (request: CommandRequest)
    ()
    =
    if rowCount owner "case_changes" request.OperationId = 1L then
        let evidence =
            healthy.EvidenceStore.TryReadEvidence(request.OperationId, Intent, ct) |> await

        let evidence =
            evidence
            |> Option.defaultWith (fun () -> failtest "Real acceptance intent is missing.")

        let plain =
            healthy.KeyCustody.Decrypt(
                evidence.Ticket.KeyId,
                WitnessProof.associatedData healthy.Identity request.OperationId "INTENT",
                evidence.EncryptedPayload
            )

        let digest =
            try
                SHA256.HashData plain
            finally
                CryptographicOperations.ZeroMemory plain

        try
            healthy.SettleAccepted(
                request.OperationId,
                {
                    Ticket = evidence.Ticket
                    CandidateHash = digest
                }
            )
            |> await
            |> ignore
        finally
            CryptographicOperations.ZeroMemory digest

        raise (TimeoutException("synthetic lost original settlement response"))

let private requireSettled owner (healthy: WitnessProtocol) operation =
    Expect.equal (rowCount owner "case_changes" operation) 1L "One real primary acceptance exists"

    for phase in [ Intent; SettledAccepted ] do
        let evidence = healthy.EvidenceStore.TryReadEvidence(operation, phase, ct) |> await
        Expect.isSome evidence "Real independent intent and settlement exist"

        Expect.equal
            evidence.Value.Ticket.OperationId
            operation
            "Witness evidence preserves exact operation identity"

let private requireReplay source healthy context request =
    match
        FixtureCommandExecution.executeRequest source healthy context clock request
        |> await
    with
    | Ok receipt ->
        Expect.isTrue
            receipt.Replayed
            "Healthy exact replay observes the unchanged accepted operation"

        Expect.equal (Claim.view receipt.Case).Version 1L "Exact recovery does not add a revision"
    | _ -> failtest "Healthy exact readback must recover durable accepted evidence."

let private qualify unavailable () =
    setup (fun owner source writer _ principal ->
        let request =
            openRequest (Guid.NewGuid()) ("SETTLED-READBACK-" + Guid.NewGuid().ToString("N"))

        use healthy = protocol owner writer (fun () -> ())
        let store = FixtureWitnessWriterStore.current writer healthy.Identity
        let keyId, _ = store.ReadKeyCheck(ct) |> await
        let inner = new KeyRing(keyId, [ keyId, witnessKey () ]) :> IKeyCustody
        let mutable checkedReadback = false

        let target =
            WitnessProof.associatedData healthy.Identity request.OperationId "SETTLED_ACCEPTED"

        let observed () =
            requireSettled owner healthy request.OperationId
            checkedReadback <- true

        use fault =
            new WitnessProtocol(
                store,
                custody inner target unavailable observed,
                healthy.Identity,
                settlePersistedAcceptance owner healthy request
            )

        let context =
            actorContext source fault principal EndpointAction.ExecuteNewCase request

        let result =
            FixtureCommandExecution.executeRequest source fault context clock request
            |> await

        match result, unavailable with
        | Error(CoreFailure.CommitOutcomeUnknown operation), true ->
            Expect.equal
                operation
                request.OperationId
                "Unavailable verification retains exact uncertainty"
        | Ok receipt, false ->
            Expect.equal
                (Claim.view receipt.Case).Version
                1L
                "Healthy fallback proves the same callback can recover definite acceptance"
        | _ -> failtest "Verification availability must determine the known outcome."

        Expect.isTrue
            checkedReadback
            "The exact decryption boundary ran after real settlement existed"

        requireSettled owner healthy request.OperationId

        requireReplay source healthy context request

        requireSettled owner healthy request.OperationId)

let tests =
    testList
        "durable witness verification knowledge"
        [
            testCase
                "[CC-WIT-001][CC-REC-001] durable settlement with unavailable exact verification preserves uncertainty"
                (qualify true)
            testCase
                "[CC-WIT-001] healthy exact fallback recovers after the same lost original settlement response"
                (qualify false)
        ]
