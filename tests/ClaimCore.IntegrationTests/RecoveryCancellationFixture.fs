module internal ClaimCore.IntegrationTests.RecoveryCancellationFixture

open System
open System.Security.Cryptography
open System.Threading
open Expecto
open ClaimCore.Application
open ClaimCore.Postgres
open ClaimCore.RecordFormat
open ClaimCore.IntegrationTests.Fixtures

let gate source =
    new PostgresActorGate(
        source,
        FixturePrivateFiles.syntheticCommitments (witnessProtocol ()).Identity
    )
    :> IActorGate

let draft source =
    let request = newRequest ()
    let canonical = RequestRecord.encode request

    let context =
        (gate source)
            .Command(
                ActorBoundStoreFixture.actorPrincipal (),
                EndpointAction.PrepareNewCase,
                request,
                CancellationToken.None
            )
        |> await
        |> Option.defaultWith (fun () -> failtest "Synthetic preparer was not admitted.")

    let material =
        {
            OperationId = request.OperationId
            CaseId = context.CaseId |> Option.defaultWith (fun () -> failtest "Case ID is absent.")
            PreparerActorId = context.Binding.ActorId
            ImporterActorId = None
            PreparerGrantRevision = context.Binding.GrantRevision
            CanonicalRequestFormat = RecordVersions.CanonicalCommandFormat
            RequestSha256 = canonical |> SHA256.HashData |> Convert.ToHexStringLower
            CanonicalRequest = canonical
            PreparingApplicationVersion = BuildIdentity.current.Version
            PreparingContractFingerprint =
                SemanticContract.fingerprint SemanticContract.current
                |> SemanticCoreFingerprint.value
            PreparingContractKind = PreparingContractKind.SemanticCoreV1
        }

    material, context

let recovery source context =
    PostgresRecoveryStore(source, PreparationLimits.defaults, witnessProtocol (), context)
    :> IRecoveryStore

let operationPort source action operationId =
    let context =
        (gate source)
            .Operation(
                ActorBoundStoreFixture.actorPrincipal (),
                action,
                operationId,
                CancellationToken.None
            )
        |> await
        |> Option.defaultWith (fun () -> failtest "Synthetic recovery operation was not admitted.")

    recovery source context
