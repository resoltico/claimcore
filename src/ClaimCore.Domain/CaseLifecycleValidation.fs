namespace ClaimCore.Domain

open System
open System.Globalization
open System.Text

module internal CaseLifecycleValidation =
    let validReason (value: string) =
        if String.IsNullOrWhiteSpace value || value <> value.Trim() then
            false
        else
            try
                UTF8Encoding(false, true).GetByteCount(value) |> ignore
                let length = value.EnumerateRunes() |> Seq.length

                let unsafeCategory rune =
                    match Rune.GetUnicodeCategory rune with
                    | UnicodeCategory.Control
                    | UnicodeCategory.Format
                    | UnicodeCategory.LineSeparator
                    | UnicodeCategory.ParagraphSeparator -> true
                    | _ -> false

                length <= 500 && not (value.EnumerateRunes() |> Seq.exists unsafeCategory)
            with :? EncoderFallbackException ->
                false

    let validInstant (value: DateTimeOffset) = value.Offset = TimeSpan.Zero

    let validRestoredHolds (holds: LifecycleHold list) =
        let ids = holds |> List.map _.Id

        ids.Length = (ids |> List.distinct |> List.length)
        && (holds
            |> List.forall (fun hold ->
                hold.Id <> Guid.Empty
                && hold.RecordedBy <> Guid.Empty
                && validReason hold.Ground
                && validInstant hold.RecordedAt))

    let checkHold (hold: LifecycleHold) =
        if hold.Id = Guid.Empty || hold.RecordedBy = Guid.Empty then
            Error LifecycleRefusal.InvalidIdentity
        elif not (validReason hold.Ground) then
            Error LifecycleRefusal.InvalidReason
        elif
            not (validInstant hold.RecordedAt)
            || hold.ReviewOn < DateOnly.FromDateTime(hold.RecordedAt.UtcDateTime)
            || hold.ReviewOn.DayNumber
               - DateOnly.FromDateTime(hold.RecordedAt.UtcDateTime).DayNumber
                >
                366
        then
            Error LifecycleRefusal.InvalidTime
        else
            Ok()

    let validDigest (digest: string) =
        not (String.IsNullOrEmpty digest)
        && digest.Length = 64
        && digest |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))

    let private checkBindingIdentity
        caseId
        revision
        referenceMismatch
        snapshotVersion
        (decision: LifecycleDecision)
        expectedAction
        =
        if
            decision.Action <> expectedAction
            || decision.CaseId <> caseId
            || referenceMismatch
        then
            Error LifecycleRefusal.WrongCase
        elif snapshotVersion <> revision || decision.ExpectedRevision <> revision then
            Error LifecycleRefusal.VersionConflict
        elif revision >= Int64.MaxValue - 1L then
            Error LifecycleRefusal.RevisionExhausted
        else
            Ok()

    let private checkBindingPayload (decision: LifecycleDecision) =
        if
            decision.OperationId = Guid.Empty
            || decision.ActorId = Guid.Empty
            || not (validDigest decision.EventDigest)
        then
            Error LifecycleRefusal.InvalidIdentity
        elif not (validInstant decision.At) then
            Error LifecycleRefusal.InvalidTime
        elif not (validReason decision.Reason) then
            Error LifecycleRefusal.InvalidReason
        else
            Ok()

    let checkBinding caseId revision referenceMismatch snapshotVersion decision expectedAction =
        match
            checkBindingIdentity
                caseId
                revision
                referenceMismatch
                snapshotVersion
                decision
                expectedAction
        with
        | Error refusal -> Error refusal
        | Ok() -> checkBindingPayload decision

    let checkErasureDecision caseId revision (decision: LifecycleDecision) action =
        if decision.Action <> action || decision.CaseId <> caseId then
            Error LifecycleRefusal.WrongCase
        elif decision.ExpectedRevision <> revision then
            Error LifecycleRefusal.VersionConflict
        elif
            decision.OperationId = Guid.Empty
            || decision.ActorId = Guid.Empty
            || not (validDigest decision.EventDigest)
        then
            Error LifecycleRefusal.InvalidIdentity
        elif not (validInstant decision.At) then
            Error LifecycleRefusal.InvalidTime
        elif not (validReason decision.Reason) then
            Error LifecycleRefusal.InvalidReason
        else
            LifecycleEvidenceAuthority.checkApprovals decision

    let checkOwnerPurgeDecision caseId revision (decision: OwnerPurgeDecision) =
        if decision.CaseId <> caseId then
            Error LifecycleRefusal.WrongCase
        elif decision.ExpectedRevision <> revision then
            Error LifecycleRefusal.VersionConflict
        elif decision.OperationId = Guid.Empty || not (validDigest decision.EventDigest) then
            Error LifecycleRefusal.InvalidIdentity
        elif not (validInstant decision.At) then
            Error LifecycleRefusal.InvalidTime
        elif not (validReason decision.Reason) then
            Error LifecycleRefusal.InvalidReason
        else
            LifecycleEvidenceAuthority.checkOwnerPurgeApprovals decision
