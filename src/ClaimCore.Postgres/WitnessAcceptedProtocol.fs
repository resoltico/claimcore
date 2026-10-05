namespace ClaimCore.Postgres

open System
open System.Threading
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
        (ct: CancellationToken)
        =
        task {
            let request = Operation.request operation
            let plain = WitnessProof.candidate operation context caseId attribution claim

            try
                return! witness.BeginAuthority(request.OperationId, plain, Some caseId, ct)
            finally
                CryptographicOperations.ZeroMemory(plain)
        }
