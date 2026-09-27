namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql
open ClaimCore.HostSecurity
open ClaimCore.Postgres

/// Owner callback reopens the actual encrypted object and recomputes the separate private
/// location commitment. The independent signer attests the isolated BASE/WAL physical drill.
[<Sealed>]
type internal DatabaseManagedCopyPhysicalVerifier(inputs: ManagedCopyPhysicalInputs) =
    let proofBytes = Array.copy inputs.Proof
    let signatureBytes = Array.copy inputs.Signature
    let mutable disposed = false

    let verify (transition: ManagedCopyTransition) (checkedAt: DateTimeOffset) =
        if disposed || inputs.CopyId <> transition.Copy.CopyId then
            None
        else
            try
                let verified = VerifiedManagedCopyRestore.FromRechecked(proofBytes, signatureBytes)
                let proof = verified.Proof
                use key = ManagedCopyCommitmentKey.Load(inputs.CommitmentKeyPath)
                let commitment = key.Commit("location", inputs.ObjectPath)

                let objectResult =
                    PrivateFileService.hashPrivateFile inputs.MaximumObjectBytes inputs.ObjectPath

                match objectResult with
                | Ok(length, digest) when
                    CryptographicOperations.FixedTimeEquals(commitment, proof.LocationCommitment)
                    && length = proof.CiphertextBytes
                    && CryptographicOperations.FixedTimeEquals(digest, proof.CiphertextSha256)
                    && proof.CheckedAt <= checkedAt
                    && proof.ValidUntil > checkedAt
                    ->
                    Some verified
                | _ -> None
            with _ ->
                None

    interface IManagedCopyPhysicalVerifier with
        member _.Verify(_, _, _, transition, _, checkedAt, ct: CancellationToken) =
            task {
                ct.ThrowIfCancellationRequested()
                return verify transition checkedAt
            }

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                CryptographicOperations.ZeroMemory(proofBytes)
                CryptographicOperations.ZeroMemory(signatureBytes)
