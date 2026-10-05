namespace ClaimCore.Postgres

open System
open System.Security.Cryptography
open System.Threading
open ClaimCore.Witness

/// Intent acquisition reports cause without turning a dispatched append failure into non-commit.
module internal WitnessIntentAdmission =
    let beginIntent
        (store: Store)
        (custody: IKeyCustody)
        associatedData
        observer
        operationId
        subjectCaseId
        (plain: byte array)
        (ct: CancellationToken)
        =
        task {
            let! existing =
                task {
                    try
                        return! store.TryReadEvidence(operationId, Intent, ct)
                    with
                    | :? OperationCanceledException -> return raise (OperationCanceledException(ct))
                    | error ->
                        WitnessFailures.report observer WitnessFailureStage.Read error
                        return raise WitnessPending
                }

            if existing.IsSome then
                raise WitnessPending

            let digest = SHA256.HashData(plain)
            let keyId = custody.ActiveKeyId
            let encrypted = custody.Encrypt(keyId, associatedData operationId "INTENT", plain)

            try
                try
                    let! ticket =
                        store.Append(operationId, subjectCaseId, Intent, keyId, encrypted, ct)

                    return
                        {
                            Ticket = ticket
                            CandidateHash = digest
                        }
                with
                | :? OperationCanceledException -> return raise (OperationCanceledException(ct))
                | error ->
                    WitnessFailures.report observer WitnessFailureStage.Append error
                    return raise WitnessPending
            finally
                CryptographicOperations.ZeroMemory(encrypted)
        }
