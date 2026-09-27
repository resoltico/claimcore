namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open ClaimCore.Application
open ClaimCore.Domain
open ClaimCore.Witness

/// Accepted business effects are the only witness-adapter operation that needs Domain values.
/// Owner administration therefore depends on the neutral witness protocol, not case-work types.
module internal WitnessAcceptedProtocol =
    let beginAccepted
        (witness: WitnessProtocol)
        (operation: PreparedOperation)
        (context: BusinessContext)
        (caseId: Guid)
        (attribution: ExecutionAttribution)
        (claim: Claim)
        =
        let request = Operation.request operation
        let store = witness.EvidenceStore
        let custody = witness.KeyCustody

        // An intent with no primary receipt remains unresolved, never a new request to replay.
        try
            if store.TryReadEvidence(request.OperationId, Intent).IsSome then
                raise WitnessPending
        with _ ->
            raise WitnessPending

        let plain = WitnessProof.candidate operation context caseId attribution claim

        try
            let digest = SHA256.HashData(plain)
            let keyId = custody.ActiveKeyId

            let encrypted =
                custody.Encrypt(keyId, witness.AssociatedData(request.OperationId, "INTENT"), plain)

            let ticket =
                try
                    store.Append(request.OperationId, Some caseId, Intent, keyId, encrypted)
                with _ ->
                    raise WitnessPending

            {
                Ticket = ticket
                CandidateHash = digest
            }
        finally
            CryptographicOperations.ZeroMemory(plain)
