namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open ClaimCore.Witness

/// Shared key and tip proof for mutation admission and owner read-only quarantine audit.
module internal WitnessCustodyAdmission =
    let verify (store: Store) (custody: IKeyCustody) (identity: Identity) =
        let keyId, check = store.ReadKeyCheck()

        if keyId <> custody.ActiveKeyId then
            invalidOp "Witness active key differs from custody."

        KeyCheck.verify custody identity.InstallationId identity.LineageId keyId check

        for required in store.RequiredKeyIds() do
            if not (custody.HasKey required) then
                invalidOp "Retained witness evidence key is unavailable."

        match store.TryReadTipEvidence() with
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
