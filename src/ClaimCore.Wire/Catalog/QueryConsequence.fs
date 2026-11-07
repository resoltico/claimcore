namespace ClaimCore.Wire

open ClaimCore.Presentation

module QueryConsequence =
    // This flat, exhaustive query table has no nested decisions; new operational queries must receive an explicit consequence.
    // fsharplint:disable-next-line CyclomaticComplexity
    let ofQuery =
        function
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
        | QueryEndpoint.OperationGet
        | QueryEndpoint.AuthorityList
        | QueryEndpoint.AuthorityGrants
        | QueryEndpoint.AuthorityHistory
        | QueryEndpoint.AuthorityRequests
        | QueryEndpoint.AuthorityRequestsDismiss
        | QueryEndpoint.Context -> Consequence.Routine
