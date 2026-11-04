namespace ClaimCore.Protocol

open ClaimCore.Domain

/// Presentation consequence is core policy, independent of a caller's current authority.
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

    let private authorityAct =
        function
        | AuthorityKind.Disable -> Consequence.Stewardship
        | AuthorityKind.EraseProfile -> Consequence.Irreversible
        | AuthorityKind.ProvisionOwner
        | AuthorityKind.Register
        | AuthorityKind.Grant
        | AuthorityKind.Revoke
        | AuthorityKind.Enable -> Consequence.Routine
        | AuthorityKind.SetProfile -> Consequence.Routine

    let ofAct =
        function
        | ActKind.Case kind -> caseAct kind
        | ActKind.Authority kind -> authorityAct kind
        | ActKind.Work WorkActKind.UpdateWork -> Consequence.Routine
        | ActKind.Reminder(ReminderActKind.Create | ReminderActKind.Update | ReminderActKind.Complete | ReminderActKind.Cancel | ReminderActKind.Reopen | ReminderActKind.CompleteAndPlanNext) ->
            Consequence.Routine

    // This flat, exhaustive query table has no nested decisions; new operational queries must receive an explicit consequence.
    // fsharplint:disable-next-line CyclomaticComplexity
    let ofQuery =
        function
        | QueryEndpoint.Resolve -> Consequence.Irreversible
        | QueryEndpoint.CaseGet
        | QueryEndpoint.CaseAssignees
        | QueryEndpoint.CaseList
        | QueryEndpoint.CaseViews
        | QueryEndpoint.ReminderViews
        | QueryEndpoint.CaseSummary
        | QueryEndpoint.CaseHistory
        | QueryEndpoint.CaseActivity
        | QueryEndpoint.CaseTimeline
        | QueryEndpoint.ReminderList
        | QueryEndpoint.ReminderGet
        | QueryEndpoint.WorkOverview
        | QueryEndpoint.Observe
        | QueryEndpoint.Status
        | QueryEndpoint.AuthorityList
        | QueryEndpoint.AuthorityGrants
        | QueryEndpoint.AuthorityHistory
        | QueryEndpoint.Context -> Consequence.Routine
