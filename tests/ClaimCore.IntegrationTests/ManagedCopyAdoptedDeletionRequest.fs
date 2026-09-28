module internal ClaimCore.IntegrationTests.ManagedCopyAdoptedDeletionRequest

open System
open System.Threading
open Expecto
open Npgsql
open NSec.Cryptography
open ClaimCore.Postgres
open ClaimCore.Witness
open ClaimCore.IntegrationTests.Fixtures

let private transitioned owner witness (value: AdoptedCopyTransition) canonical signature =
    use connection = new NpgsqlConnection(owner)
    connection.Open()

    match
        ManagedCopyAdoptedTransitionAdministration.transition connection witness canonical signature
        |> await
    with
    | AuthorityWriteOutcome.Applied(id, revision) when
        id = value.EventId && revision = value.Revision
        ->
        ()
    | _ -> failtest "Adopted deletion request failed."

let create
    owner
    (witness: WitnessProtocol)
    (origin: VerifiedCopyAdoptionOrigin)
    (unknown: AdoptedCopyTransition)
    unknownCanonical
    unknownSignature
    (copyKey: Key)
    (algorithm: SignatureAlgorithm)
    =
    while DateTimeOffset.UtcNow < origin.RetainUntil do
        Thread.Sleep(100)

    let prior =
        ManagedCopyEventHash.compute origin.CopyEventHash unknownCanonical (Some unknownSignature)

    let tip = witness.Snapshot()

    let request =
        { unknown with
            EventId = Guid.NewGuid()
            Revision = unknown.Revision + 1L
            EventKind = "DELETE_REQUEST"
            State = "DELETE_PENDING"
            PreviousEventHash = prior
            ActionWitnessCutoffSequence = tip.TipSequence
            ActionWitnessCutoffHash = tip.TipHash
        }

    let canonical = ManagedCopyAdoptedTransitionAttestation.encode request
    let signature = algorithm.Sign(copyKey, canonical)
    transitioned owner witness request canonical signature
    prior, request, canonical, signature
