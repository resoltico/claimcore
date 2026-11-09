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
        Endpoint: QueryTaskId
        Inputs: ReadInput list
    }


module ReadCatalog =
    let declarations =
        [
            {
                Endpoint = QueryTaskId.CaseNotes
                Inputs = [ ReadInput.CaseOperand; ReadInput.Page ]
            }
            {
                Endpoint = QueryTaskId.CaseTimeline
                Inputs = [ ReadInput.CaseOperand; ReadInput.TimelineFilter; ReadInput.Page ]
            }
            {
                Endpoint = QueryTaskId.ReminderGet
                Inputs = [ ReadInput.CaseOperand; ReadInput.ReminderIdentity ]
            }
            {
                Endpoint = QueryTaskId.ReminderList
                Inputs = [ ReadInput.Reminders; ReadInput.Page ]
            }
            {
                Endpoint = QueryTaskId.CaseAssignees
                Inputs = [ ReadInput.CaseOperand; ReadInput.Page ]
            }
            {
                Endpoint = QueryTaskId.CaseGet
                Inputs = [ ReadInput.CaseOperand ]
            }
            {
                Endpoint = QueryTaskId.CaseList
                Inputs = [ ReadInput.Page; ReadInput.Worklist ]
            }
            {
                Endpoint = QueryTaskId.CaseSummary
                Inputs = [ ReadInput.Worklist ]
            }
            {
                Endpoint = QueryTaskId.WorkViews
                Inputs = []
            }
            {
                Endpoint = QueryTaskId.OperationGet
                Inputs = [ ReadInput.OperationOperand ]
            }
            {
                Endpoint = QueryTaskId.AuthorityList
                Inputs = [ ReadInput.DirectorySearch; ReadInput.ActorRole; ReadInput.Page ]
            }
            {
                Endpoint = QueryTaskId.AuthorityGrants
                Inputs = [ ReadInput.ActorOperand; ReadInput.Page ]
            }
            {
                Endpoint = QueryTaskId.AuthorityHistory
                Inputs = [ ReadInput.ActorFilter; ReadInput.Page ]
            }
            {
                Endpoint = QueryTaskId.AuthorityRequests
                Inputs = [ ReadInput.Page ]
            }
            {
                Endpoint = QueryTaskId.AuthorityRequestsDismiss
                Inputs = [ ReadInput.PrincipalOperand ]
            }
            {
                Endpoint = QueryTaskId.Context
                Inputs = []
            }
        ]
