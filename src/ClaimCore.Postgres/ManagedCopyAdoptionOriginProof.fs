namespace ClaimCore.Postgres

open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Witness
open WitnessProtocolReconciliation

/// The external branch proves an exact signed pre-fence publication and its encryption key;
/// a historical witness hash by itself never establishes an unmanaged copy.
module internal ManagedCopyAdoptionOriginProof =
    let verify
        connection
        transaction
        (witness: WitnessProtocol)
        (request: CopyAdoptionApprovalRequest)
        encryptionKeyId
        (ct: CancellationToken)
        =
        task {
            match request.Origin with
            | CopyAdoptionOrigin.ProductExport(_, sequence, hash) ->
                try
                    do! witness.VerifyHistoricalTip(sequence, hash, ct)
                    return true
                with _ ->
                    return false
            | CopyAdoptionOrigin.AdoptedExternal _ ->
                let! snapshot = witness.Snapshot(ct)
                let cutoff = snapshot.TipSequence

                let! exact =
                    ManagedCopyExternalPublicationOrigin.verify
                        connection
                        transaction
                        witness
                        cutoff
                        request
                        ct

                if not exact then
                    return false
                else
                    let! published =
                        ManagedCopyExternalPublicationEvidence.verifyCopy
                            connection
                            transaction
                            witness
                            cutoff
                            request.CopyId
                            ct

                    return
                        published
                        |> Option.exists (fun value -> value.EncryptionKeyId = encryptionKeyId)
        }
