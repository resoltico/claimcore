namespace ClaimCore.Application


open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat
open ClaimCore.Application.VerificationChecks

[<RequireQualifiedAccess; NoComparison>]
type internal VerifiedPayload =
    | NonAcceptance of OperationId
    | Case of CaseCommand * CaseSnapshot option
    | Authority of AuthorityRequestRecord
    | Erased

/// Record-format interpretation deliberately excludes current admission and capability policy.
module internal VerificationPayload =
    let private problem sequence =
        divergence (Some sequence) VerificationFinding.PayloadFormat

    let private decoded (entry: StoredEntry) =
        Result.mapError (fun _ -> problem entry.Sequence)

    let private case _installationId (entry: StoredEntry) (payload: LedgerPayload) =
        match entry.Subject with
        | EntryTarget.Authority _
        | EntryTarget.NonAcceptance _ ->
            Error(divergence (Some entry.Sequence) VerificationFinding.PayloadFormat)
        | EntryTarget.Case(id, revision, act, _) ->
            result {
                let! request =
                    RequestRecord.decode RecordLimits.MaximumRequestRecordBytes payload.Request
                    |> decoded entry

                do!
                    require
                        (Some entry.Sequence)
                        VerificationFinding.PayloadFormat
                        (request.OperationId = entry.OperationId
                         && Revision.value request.ExpectedRevision = Revision.value revision - 1L
                         && CaseAct.kind request.Act = act
                         && (act <> CaseActKind.Open
                             || (id = CaseId.ofOperationId request.OperationId
                                 && entry.CreationReference = Some request.Reference)))

                if act = CaseActKind.Erase then
                    do!
                        require
                            (Some entry.Sequence)
                            VerificationFinding.PayloadFormat
                            (Array.isEmpty payload.Snapshot)

                    return VerifiedPayload.Case(request, None)
                else
                    let! snapshot =
                        ClaimCore.RecordFormat.CaseRecord.decodeSnapshot payload.Snapshot
                        |> decoded entry

                    do!
                        require
                            (Some entry.Sequence)
                            VerificationFinding.PayloadFormat
                            (snapshot.CaseId = id
                             && snapshot.Revision = revision
                             && CaseReference.identity snapshot.Reference =
                                 CaseReference.identity request.Reference)

                    return VerifiedPayload.Case(request, Some snapshot)
            }

    let private authority (entry: StoredEntry) kind (payload: LedgerPayload) =
        result {
            let! request =
                AuthorityRecord.decode RecordLimits.MaximumRequestRecordBytes payload.Request
                |> decoded entry

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.PayloadFormat
                    (request.OperationId = OperationId.value entry.OperationId
                     && AuthorityAct.kind request.Act = kind
                     && Array.isEmpty payload.Snapshot)

            return VerifiedPayload.Authority request
        }

    let read installationId (entry: StoredEntry) =
        match entry.Payload, entry.PayloadNonce, entry.Subject with
        | None, None, EntryTarget.Case _ -> Ok VerifiedPayload.Erased
        | Some bytes, Some nonce, _ ->
            result {
                let! commitment =
                    capture (Some entry.Sequence) VerificationFinding.Commitment (fun () ->
                        LedgerEntry.commitment nonce bytes)

                do!
                    require
                        (Some entry.Sequence)
                        VerificationFinding.Commitment
                        (commitment = entry.PayloadCommitment)

                let! payload =
                    LedgerEntry.decodePayload LedgerPayload.MaximumBytes bytes |> decoded entry

                return!
                    match entry.Subject with
                    | EntryTarget.NonAcceptance operation ->
                        require
                            (Some entry.Sequence)
                            VerificationFinding.PayloadFormat
                            (entry.OperationId = operation
                             && entry.CreationReference.IsNone
                             && Array.isEmpty payload.Snapshot
                             && (ControlRecord.decode payload.Request |> Result.isOk))
                        |> Result.map (fun () -> VerifiedPayload.NonAcceptance operation)
                    | EntryTarget.Case _ -> case installationId entry payload
                    | EntryTarget.Authority(_, kind) -> authority entry kind payload
            }
        | _ -> Error(problem entry.Sequence)
