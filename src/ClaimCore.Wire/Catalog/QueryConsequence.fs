namespace ClaimCore.Wire

open ClaimCore.Application.Contracts
open ClaimCore.Presentation

module QueryConsequence =
    // This flat, exhaustive query table has no nested decisions; new operational queries must receive an explicit consequence.
    let ofQuery =
        function
        | QueryTaskId.CaseNotes
        | QueryTaskId.CaseGet
        | QueryTaskId.CaseAssignees
        | QueryTaskId.CaseList
        | QueryTaskId.CaseSummary
        | QueryTaskId.CaseTimeline
        | QueryTaskId.ReminderList
        | QueryTaskId.ReminderGet
        | QueryTaskId.WorkViews
        | QueryTaskId.OperationGet
        | QueryTaskId.AuthorityList
        | QueryTaskId.AuthorityGrants
        | QueryTaskId.AuthorityHistory
        | QueryTaskId.AuthorityRequests
        | QueryTaskId.AuthorityRequestsDismiss
        | QueryTaskId.Context -> Consequence.Routine
