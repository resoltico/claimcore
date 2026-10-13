namespace ClaimCore.Domain

open System

/// Technical state of a case record, outside the thirteen business fields.
/// An erased case has no retained business state, so it is not a disposition of a `Claim`.
[<RequireQualifiedAccess>]
type CaseDisposition =
    | Active
    | Voided

/// Data-governance acts on a case record. They are not business commands: they assert nothing
/// about the claim and never change one of the thirteen fields.
[<RequireQualifiedAccess>]
type StewardshipAction =
    /// The case was recorded in error. History and reference are preserved; case work is blocked.
    | Void of reason: string
    /// A voided case returns to ordinary case work.
    | Reinstate of reason: string
    /// The business payload of the case and its whole history is removed from the live database.
    | Erase

[<RequireQualifiedAccess>]
type StewardshipKind =
    | Void
    | Reinstate
    | Erase

/// Same technical controls as `CommandRequest`: one caller-chosen identity, one expected revision.
type StewardshipRequest =
    {
        OperationId: Guid
        CaseReference: string
        ExpectedVersion: int64
        Action: StewardshipAction
    }

[<RequireQualifiedAccess>]
type StewardshipError =
    | InvalidReason
    | VersionConflict of actualVersion: int64
    | RevisionExhausted
    | AlreadyVoided
    | NotVoided

/// What an accepted stewardship act leaves behind.
[<RequireQualifiedAccess>]
type StewardshipResult =
    | Retained of revision: int64 * disposition: CaseDisposition
    | Erased of revision: int64

module Stewardship =
    let kind =
        function
        | StewardshipAction.Void _ -> StewardshipKind.Void
        | StewardshipAction.Reinstate _ -> StewardshipKind.Reinstate
        | StewardshipAction.Erase -> StewardshipKind.Erase

    /// 1 to 200 Unicode scalar values, no surrounding whitespace, and no control, format,
    /// line-separator or paragraph-separator character.
    let private validReason (reason: string) =
        if String.IsNullOrWhiteSpace reason || reason <> reason.Trim() then
            false
        else
            try
                Text.UTF8Encoding(false, true).GetByteCount(reason) |> ignore
                let length = reason.EnumerateRunes() |> Seq.length

                let forbidden rune =
                    match Text.Rune.GetUnicodeCategory rune with
                    | Globalization.UnicodeCategory.Control
                    | Globalization.UnicodeCategory.Format
                    | Globalization.UnicodeCategory.LineSeparator
                    | Globalization.UnicodeCategory.ParagraphSeparator -> true
                    | _ -> false

                length <= 200 && not (reason.EnumerateRunes() |> Seq.exists forbidden)
            with :? Text.EncoderFallbackException ->
                false

    /// Pure decision over the current revision and disposition. Every accepted act advances the
    /// case revision exactly once, like every accepted business command.
    let decide
        (request: StewardshipRequest)
        (currentRevision: int64)
        (disposition: CaseDisposition)
        : Result<StewardshipResult, StewardshipError> =
        if request.ExpectedVersion <> currentRevision then
            Error(StewardshipError.VersionConflict currentRevision)
        elif currentRevision >= Int64.MaxValue - 1L then
            Error StewardshipError.RevisionExhausted
        else
            let next = currentRevision + 1L

            match request.Action, disposition with
            | StewardshipAction.Void reason, _
            | StewardshipAction.Reinstate reason, _ when not (validReason reason) ->
                Error StewardshipError.InvalidReason
            | StewardshipAction.Void _, CaseDisposition.Voided ->
                Error StewardshipError.AlreadyVoided
            | StewardshipAction.Void _, CaseDisposition.Active ->
                Ok(StewardshipResult.Retained(next, CaseDisposition.Voided))
            | StewardshipAction.Reinstate _, CaseDisposition.Active ->
                Error StewardshipError.NotVoided
            | StewardshipAction.Reinstate _, CaseDisposition.Voided ->
                Ok(StewardshipResult.Retained(next, CaseDisposition.Active))
            | StewardshipAction.Erase, _ -> Ok(StewardshipResult.Erased next)
