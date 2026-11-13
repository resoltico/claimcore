namespace ClaimCore.Presentation

open ClaimCore.Application.Contracts
open ClaimCore.Domain

/// Presentation consequence is core policy, independent of a caller's current people.
[<RequireQualifiedAccess>]
type Consequence =
    | Routine
    | Stewardship
    | Irreversible

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Consequence =
    let vocabulary =
        Vocabulary.ofUnion<Consequence> "consequence" TokenCasing.ScreamingSnake None

    let private caseAct kind =
        let description = CaseActKind.describe kind

        match description.Effect with
        | CaseEffect.ErasePayload -> Consequence.Irreversible
        | CaseEffect.RecordFacts
        | CaseEffect.ChangeStatus
        | CaseEffect.ChangeLifecycle ->
            match description.Family with
            | ActFamily.Business -> Consequence.Routine
            | ActFamily.Stewardship -> Consequence.Stewardship

    let private peopleAct =
        function
        | PeopleActKind.Disable -> Consequence.Stewardship
        | PeopleActKind.EraseProfile -> Consequence.Irreversible
        | PeopleActKind.ProvisionOwner
        | PeopleActKind.Register
        | PeopleActKind.Admit
        | PeopleActKind.Grant
        | PeopleActKind.Revoke
        | PeopleActKind.Enable -> Consequence.Routine
        | PeopleActKind.SetProfile -> Consequence.Routine

    let ofAct =
        function
        | ActKind.Case kind -> caseAct kind
        | ActKind.People kind -> peopleAct kind
        | ActKind.Note _ -> Consequence.Routine
        | ActKind.Work WorkActKind.UpdateWork -> Consequence.Routine
        | ActKind.Reminder(ReminderActKind.Create | ReminderActKind.Update | ReminderActKind.Complete | ReminderActKind.Cancel | ReminderActKind.Reopen | ReminderActKind.CompleteAndPlanNext) ->
            Consequence.Routine
