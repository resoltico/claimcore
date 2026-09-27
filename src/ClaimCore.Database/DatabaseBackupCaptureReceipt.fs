namespace ClaimCore.Database

open System
open System.Security.Cryptography
open System.Text

/// A local exact-byte receipt identifier. It is not an independent-host or restore certificate.
module internal DatabaseBackupCaptureReceipt =
    let digest (cycleId: Guid) (leaseId: Guid) nonce (files: BackupCaptureFileDigests) =
        String.Join(
            "|",
            [|
                cycleId.ToString("D")
                leaseId.ToString("D")
                nonce
                files.Primary.Sha256
                files.Witness.Sha256
                files.Checkpoint.Sha256
                files.CheckpointSignature.Sha256
                files.Manifest.Sha256
                files.ManifestSignature.Sha256
            |]
        )
        |> Encoding.ASCII.GetBytes
        |> SHA256.HashData
        |> Convert.ToHexStringLower
