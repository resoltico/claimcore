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
        Endpoint: TaskId
        Inputs: ReadInput list
    }


module ReadCatalog =
    let declarations =
        [
            {
                Endpoint = TaskId.Query QueryTaskId.CasePayments
                Inputs = [ ReadInput.CaseOperand; ReadInput.Page ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.CaseNotes
                Inputs = [ ReadInput.CaseOperand; ReadInput.Page ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.CaseTimeline
                Inputs = [ ReadInput.CaseOperand; ReadInput.TimelineFilter; ReadInput.Page ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.ReminderGet
                Inputs = [ ReadInput.CaseOperand; ReadInput.ReminderIdentity ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.ReminderList
                Inputs = [ ReadInput.Reminders; ReadInput.Page ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.CaseAssignees
                Inputs = [ ReadInput.CaseOperand; ReadInput.Page ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.CaseGet
                Inputs = [ ReadInput.CaseOperand ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.CaseList
                Inputs = [ ReadInput.Page; ReadInput.Worklist ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.CaseSummary
                Inputs = [ ReadInput.Worklist ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.WorkViews
                Inputs = []
            }
            {
                Endpoint = TaskId.Query QueryTaskId.OperationGet
                Inputs = [ ReadInput.OperationOperand ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.PeopleList
                Inputs = [ ReadInput.DirectorySearch; ReadInput.ActorRole; ReadInput.Page ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.PeopleGrants
                Inputs = [ ReadInput.ActorOperand; ReadInput.Page ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.PeopleHistory
                Inputs = [ ReadInput.ActorFilter; ReadInput.Page ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.PeopleRequests
                Inputs = [ ReadInput.Page ]
            }
            {
                Endpoint = TaskId.Command CommandTaskId.PeopleRequestsDismiss
                Inputs = [ ReadInput.PrincipalOperand ]
            }
            {
                Endpoint = TaskId.Query QueryTaskId.Context
                Inputs = []
            }
        ]
