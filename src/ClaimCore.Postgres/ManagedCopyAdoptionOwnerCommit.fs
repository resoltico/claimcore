namespace ClaimCore.Postgres

open System.Security.Cryptography
open System.Threading
open ClaimCore.Witness

/// A CASE witness intent precedes the one primary transaction containing projection,
/// immutable signed event, adoption receipt and one-use human approval consumption.
module internal ManagedCopyAdoptionOwnerCommit =
    let private persist
        connection
        (transaction: Npgsql.NpgsqlTransaction)
        (witness: WitnessProtocol)
        (value: CopyAdoptionOwnerEventData)
        canonical
        =
        task {
            let request = value.Approval.Request
            let submission = value.Submission

            let intent =
                witness.BeginAuthority(submission.AdoptionEventId, canonical, Some request.CaseId)

            do!
                ManagedCopyAdoptionOwnerProjectionWrite.apply
                    connection
                    transaction
                    witness
                    request
                    value.Documents.Custody
                    value.CopyEventHash

            do!
                ManagedCopyAdoptionOwnerProjectionWrite.event
                    connection
                    transaction
                    request
                    value.Documents.Custody
                    submission
                    value.PreviousEventHash
                    value.CopyEventHash
                    intent

            do!
                ManagedCopyAdoptionOwnerReceiptWrite.insert
                    connection
                    transaction
                    value
                    canonical
                    intent

            do!
                ManagedCopyAdoptionOwnerReceiptWrite.useApproval
                    connection
                    transaction
                    request.ApprovalId
                    submission.AdoptionEventId
                    request.CaseId

            do! transaction.CommitAsync(CancellationToken.None)
            return intent
        }

    let private settle
        (witness: WitnessProtocol)
        (value: CopyAdoptionOwnerEventData)
        (intent: WitnessIntent)
        =
        try
            let request = value.Approval.Request
            let eventId = value.Submission.AdoptionEventId
            witness.SettleAuthority(eventId, intent) |> ignore

            witness.VerifyAuthorityEvidenceForCase(
                eventId,
                intent.Ticket.Sequence,
                intent.Ticket.Epoch,
                intent.Ticket.EntryHash,
                intent.CandidateHash,
                request.CaseId
            )

            CopyAdoptionOwnerOutcome.Adopted(eventId, value.Documents.Custody.Revision)
        with _ ->
            CopyAdoptionOwnerOutcome.Unconfirmed value.Submission.AdoptionEventId

    let commit connection transaction witness (value: CopyAdoptionOwnerEventData) =
        task {
            let canonical = ManagedCopyAdoptionOwnerCandidate.encode value

            try
                try
                    let! intent = persist connection transaction witness value canonical
                    return settle witness value intent
                with _ ->
                    return CopyAdoptionOwnerOutcome.Unconfirmed value.Submission.AdoptionEventId
            finally
                CryptographicOperations.ZeroMemory(canonical)
        }
