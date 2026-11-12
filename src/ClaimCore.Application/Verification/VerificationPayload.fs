namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.RecordFormat
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess; NoComparison>]
type internal VerifiedPayload =
    | Case of CaseCommand * CaseDecision option
    | Authority of AuthorityEffectDescriptor * ActorProfile option
    | Work of WorkDecisionInput * CaseWorkSnapshot
    | Reminder of ReminderDecisionInput * ReminderEffects
    | Note of NoteDecisionInput * Note
    | Erased
    | ErasedProfile

/// Reads and verification share retained framing/content admission; replay independently decides effects.
module internal VerificationPayload =
    let read installationId (entry: StoredEffect) =
        LedgerEntries.validate installationId entry
        |> Result.map LedgerEntries.content
        |> Result.map (function
            | EntryContent.Case(request, decision) -> VerifiedPayload.Case(request, decision)
            | EntryContent.Authority(change, profile) -> VerifiedPayload.Authority(change, profile)
            | EntryContent.Work(request, after) -> VerifiedPayload.Work(request, after)
            | EntryContent.Reminder(request, effects) -> VerifiedPayload.Reminder(request, effects)
            | EntryContent.Note(request, after) -> VerifiedPayload.Note(request, after)
            | EntryContent.ErasedCase -> VerifiedPayload.Erased
            | EntryContent.ErasedProfile -> VerifiedPayload.ErasedProfile)
        |> Result.mapError (fun fault ->
            {
                Sequence = Some entry.Sequence
                Finding =
                    if fault = CoreFault.OperationContentConflict then
                        VerificationFinding.Commitment
                    else
                        VerificationFinding.PayloadFormat
            })
