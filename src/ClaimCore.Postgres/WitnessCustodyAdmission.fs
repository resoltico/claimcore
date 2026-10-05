namespace ClaimCore.Postgres

open System
open System.Threading
open System.Security.Cryptography
open ClaimCore.Witness

/// Shared key and tip proof for mutation admission and owner read-only quarantine audit.
module internal WitnessCustodyAdmission =
    let verify (store: Store) (custody: IKeyCustody) (identity: Identity) (ct: CancellationToken) =
        task {
            let! keyId, check = store.ReadKeyCheck(ct)

            if keyId <> custody.ActiveKeyId then
                invalidOp "Witness active key differs from custody."

            KeyCheck.verify custody identity.InstallationId identity.LineageId keyId check

            let! requiredKeys = store.RequiredKeyIds(ct)

            for required in requiredKeys do
                if not (custody.HasKey required) then
                    invalidOp "Retained witness evidence key is unavailable."

            let! tip = store.TryReadTipEvidence(ct)

            match tip with
            | None -> ()
            | Some evidence ->
                let phase = ClaimCore.Witness.Encoding.phase evidence.Ticket.Phase

                let plain =
                    custody.Decrypt(
                        evidence.Ticket.KeyId,
                        WitnessProof.associatedData identity evidence.Ticket.OperationId phase,
                        evidence.EncryptedPayload
                    )

                CryptographicOperations.ZeroMemory(plain)
        }
