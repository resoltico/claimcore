namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Npgsql

/// A Database-owned issuer mints this only after signed proof, current key/holder and
/// actual encrypted object bytes have been independently rechecked.
[<Sealed>]
type internal VerifiedManagedCopyRestore
    private (proof: ManagedCopyPhysicalProof, canonical: byte array, signature: byte array) =
    let canonicalBytes = Array.copy canonical
    let signatureBytes = Array.copy signature
    let proofHash = SHA256.HashData(canonicalBytes)

    let cloneProof () =
        { proof with
            Nonce = Array.copy proof.Nonce
            WitnessCutoffHash = Array.copy proof.WitnessCutoffHash
            LocationCommitment = Array.copy proof.LocationCommitment
            CiphertextSha256 = Array.copy proof.CiphertextSha256
            DecryptedSha256 = Array.copy proof.DecryptedSha256
            BackupManifestSha256 = proof.BackupManifestSha256 |> Option.map Array.copy
            PgVerifyBackupManifestSha256 =
                proof.PgVerifyBackupManifestSha256 |> Option.map Array.copy
        }

    member _.Proof = cloneProof ()
    member _.Canonical = Array.copy canonicalBytes
    member _.Signature = Array.copy signatureBytes
    member _.ReportSha256 = Array.copy proofHash

    static member internal FromRechecked(canonical: byte array, signature: byte array) =
        if isNull (box signature) || signature.Length <> 64 then
            invalidArg (nameof signature) "Physical copy signature is invalid."

        let proof =
            ManagedCopyPhysicalProofCodec.parse canonical
            |> Option.defaultWith (fun () ->
                invalidArg (nameof canonical) "Physical copy proof is invalid.")

        VerifiedManagedCopyRestore(proof, canonical, signature)

/// The verifier performs a handle-first rehash and purpose-signature check while owner
/// authority and the exact managed-copy revision are held. A digest or Boolean is insufficient.
type internal IManagedCopyPhysicalVerifier =
    abstract Verify:
        primary: NpgsqlConnection *
        transaction: NpgsqlTransaction *
        witness: WitnessProtocol *
        transition: ManagedCopyTransition *
        current: ManagedCopyCurrent *
        checkedAt: DateTimeOffset *
        cancellationToken: CancellationToken ->
            Task<VerifiedManagedCopyRestore option>
