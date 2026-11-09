namespace ClaimCore.Wire

open ClaimCore.Presentation

module QueryConsequence =
    // This flat, exhaustive query table has no nested decisions; new operational queries must receive an explicit consequence.
    let ofQuery =
        function
        | QueryEndpoint.CaseNotes
        | QueryEndpoint.CaseGet
        | QueryEndpoint.CaseAssignees
        | QueryEndpoint.CaseList
        | QueryEndpoint.CaseSummary
        | QueryEndpoint.CaseTimeline
        | QueryEndpoint.ReminderList
        | QueryEndpoint.ReminderGet
        | QueryEndpoint.WorkViews
        | QueryEndpoint.OperationGet
        | QueryEndpoint.AuthorityList
        | QueryEndpoint.AuthorityGrants
        | QueryEndpoint.AuthorityHistory
        | QueryEndpoint.AuthorityRequests
        | QueryEndpoint.AuthorityRequestsDismiss
        | QueryEndpoint.Context -> Consequence.Routine
