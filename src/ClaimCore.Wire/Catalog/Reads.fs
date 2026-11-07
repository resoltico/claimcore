namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess>]
type ReadInput =
    | CaseOperand
    | OperationOperand
    | ActorOperand
    | PrincipalOperand
    | ActorFilter
    | Page
    | DirectorySearch
    | ActorRole
    | Worklist
    | Reminders
    | ReminderIdentity
    | TimelineFilter

type ReadDeclaration =
    {
        Endpoint: QueryEndpoint
        Inputs: ReadInput list
    }


module ReadCatalog =
    let declarations =
        [
            {
                Endpoint = QueryEndpoint.CaseTimeline
                Inputs = [ ReadInput.CaseOperand; ReadInput.TimelineFilter; ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.ReminderGet
                Inputs = [ ReadInput.CaseOperand; ReadInput.ReminderIdentity ]
            }
            {
                Endpoint = QueryEndpoint.ReminderList
                Inputs = [ ReadInput.Reminders; ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.CaseAssignees
                Inputs = [ ReadInput.CaseOperand; ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.CaseGet
                Inputs = [ ReadInput.CaseOperand ]
            }
            {
                Endpoint = QueryEndpoint.CaseList
                Inputs = [ ReadInput.Page; ReadInput.Worklist ]
            }
            {
                Endpoint = QueryEndpoint.CaseSummary
                Inputs = [ ReadInput.Worklist ]
            }
            {
                Endpoint = QueryEndpoint.WorkViews
                Inputs = []
            }
            {
                Endpoint = QueryEndpoint.OperationGet
                Inputs = [ ReadInput.OperationOperand ]
            }
            {
                Endpoint = QueryEndpoint.AuthorityList
                Inputs = [ ReadInput.DirectorySearch; ReadInput.ActorRole; ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.AuthorityGrants
                Inputs = [ ReadInput.ActorOperand; ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.AuthorityHistory
                Inputs = [ ReadInput.ActorFilter; ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.AuthorityRequests
                Inputs = [ ReadInput.Page ]
            }
            {
                Endpoint = QueryEndpoint.AuthorityRequestsDismiss
                Inputs = [ ReadInput.PrincipalOperand ]
            }
            {
                Endpoint = QueryEndpoint.Context
                Inputs = []
            }
        ]
