namespace ClaimCore.Application

open ClaimCore.Contract

open System
open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application.VerificationChecks

[<RequireQualifiedAccess; NoComparison>]
type internal VerifiedPayload =
    | Case of CaseCommand * CaseSnapshot option
    | Authority of AuthorityRequestRecord
    | Erased

/// Record-format interpretation deliberately excludes current admission and capability policy.
module internal VerificationPayload =
    let private problem sequence =
        divergence (Some sequence) VerificationFinding.PayloadFormat

    let private decoded (entry: StoredEntry) =
        Result.mapError (fun _ -> problem entry.Sequence)

    let private case installationId (entry: StoredEntry) id revision act (payload: LedgerPayload) =
        result {
            let! request =
                RequestRecord.decode Limits.MaximumRequestBytes payload.Request |> decoded entry

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.PayloadFormat
                    (request.OperationId = entry.OperationId
                     && request.ExpectedRevision = revision - 1L
                     && CaseActs.kind request.Act = act
                     && CaseReferenceId.encode
                         installationId
                         (CaseReference.format request.Reference)
                         =
                         id)

            if act = (CaseActKind.Stewardship StewardshipActKind.Erase) then
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
                        (snapshot.Revision = revision
                         && CaseReference.identity snapshot.Reference =
                             CaseReference.identity request.Reference)

                return VerifiedPayload.Case(request, Some snapshot)
        }

    let private authority (entry: StoredEntry) kind (payload: LedgerPayload) =
        result {
            let! request =
                AuthorityRecord.decode Limits.MaximumRequestBytes payload.Request
                |> decoded entry

            do!
                require
                    (Some entry.Sequence)
                    VerificationFinding.PayloadFormat
                    (request.OperationId = entry.OperationId
                     && RecordedAuthorityActs.kind request.Act = kind
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
                    LedgerEntry.decodePayload Formats.MaximumPayloadBytes bytes |> decoded entry

                return!
                    match entry.Subject with
                    | EntryTarget.Case(id, revision, act, _) ->
                        case installationId entry id revision act payload
                    | EntryTarget.Authority(_, kind) -> authority entry kind payload
            }
        | _ -> Error(problem entry.Sequence)
