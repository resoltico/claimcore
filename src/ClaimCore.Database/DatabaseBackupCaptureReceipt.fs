namespace ClaimCore.Database

open System
open System.Text.Json
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

    let expected (held: BackupCaptureHeld) cycleId leaseId digests =
        {
            Nonce = held.Nonce
            LeaseId = leaseId
            CycleReceiptId = cycleId
            WitnessSequence = held.Cutoff.WitnessSequence
            WitnessHash = Convert.ToHexStringLower held.Cutoff.WitnessHash
            ReceiptSha256 = digest cycleId leaseId held.Nonce digests
        }

    let parse bytes =
        DatabaseRestoreCanonical.parse bytes
        |> Option.defaultWith (fun () -> invalidOp "Backup capture readback file is noncanonical.")

    let text name (root: JsonElement) =
        DatabaseRestoreCanonical.text name root
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Backup capture readback field is missing.")

    let id name root =
        let raw = text name root

        match Guid.TryParseExact(raw, "D") with
        | true, value when value <> Guid.Empty && value.ToString("D") = raw -> value
        | _ -> invalidOp "Backup capture readback identity is invalid."


    let cycleIdentity (bytes: byte array) leaseId =
        use receiptDocument = parse bytes
        let receiptRoot = receiptDocument.RootElement

        if
            not (
                DatabaseRestoreCanonical.exactProperties
                    [
                        "cycleReceiptId"
                        "format"
                        "kind"
                        "leaseId"
                        "nonce"
                        "receiptSha256"
                        "witnessHash"
                        "witnessSequence"
                    ]
                    receiptRoot
            )
            || text "format" receiptRoot <> "claimcore-backup-barrier-frame-1"
            || text "kind" receiptRoot <> "SEALED"
            || id "leaseId" receiptRoot <> leaseId
        then
            invalidOp "Backup capture persisted receipt is not exact."

        id "cycleReceiptId" receiptRoot
