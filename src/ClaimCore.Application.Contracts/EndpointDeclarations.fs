namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

[<RequireQualifiedAccess>]
type EndpointKind =
    | Query
    | Command

/// These are use-case bounds, shared by transport admission and command acquisition.
type EndpointPage =
    {
        DefaultLimit: int
        MaximumLimit: int
        MaximumCursorCharacters: int
    }

type EndpointDeclaration =
    {
        Id: string
        Kind: EndpointKind
        Inputs: TaskInputDeclaration list
        Page: EndpointPage option
        Csv: bool
    }

    member this.Paged = this.Page.IsSome

module EndpointDeclarations =
    let private required input =
        TaskInputDeclaration.create TaskInputMode.Required input

    let private optional input =
        TaskInputDeclaration.create TaskInputMode.Optional input

    let private target input = TaskInputId.Target input
    let private field input = target (InputTarget.Field input)
    let private caseTarget = required (field BusinessField.CaseReference)

    let private page =
        [ optional (target InputTarget.Cursor); optional (target InputTarget.Limit) ]

    let private principal =
        [
            InputTarget.PrincipalKind
            InputTarget.PrincipalIssuer
            InputTarget.PrincipalSubject
        ]
        |> List.map (target >> required)

    let private worklist =
        [
            TaskInputId.Lifecycle
            TaskInputId.Status
            TaskInputId.Progress
            TaskInputId.CaseView
            TaskInputId.ResponsibleActorFilter
            TaskInputId.Assignment
            TaskInputId.Due
            TaskInputId.Order
            target InputTarget.Text
            yield! CaseDateFilter.all |> List.map TaskInputId.CaseDate
        ]
        |> List.map (fun input ->
            { optional input with
                Multiple =
                    List.contains
                        input
                        [ TaskInputId.Lifecycle; TaskInputId.Status; TaskInputId.Progress ]
            })

    let private paging cursor maximum fallback =
        Some
            {
                DefaultLimit = fallback
                MaximumLimit = maximum
                MaximumCursorCharacters = cursor
            }

    let private ordinaryPage =
        paging Paging.MaximumActorCursorCharacters Paging.MaximumPageSize Paging.DefaultPageSize

    let private query id inputs page csv =
        {
            Id = QueryTaskId.key id
            Kind = EndpointKind.Query
            Inputs =
                inputs
                |> List.map (fun input ->
                    { input with
                        Source = InputSource.Authored
                    })
            Page = page
            Csv = csv
        }

    let queries =
        [
            query QueryTaskId.CaseGet [ caseTarget ] None false
            query QueryTaskId.CaseAssignees (caseTarget :: page) ordinaryPage false
            query
                QueryTaskId.CaseList
                (worklist @ page)
                (paging
                    Paging.MaximumCaseCursorCharacters
                    Paging.MaximumPageSize
                    Paging.DefaultPageSize)
                true
            query QueryTaskId.CaseSummary (worklist @ [ optional TaskInputId.GroupBy ]) None true
            query
                QueryTaskId.CaseTimeline
                (caseTarget :: optional TaskInputId.TimelineFilter :: page)
                ordinaryPage
                true
            query
                QueryTaskId.CaseNotes
                (caseTarget :: page)
                (paging
                    Paging.MaximumActorCursorCharacters
                    NotePaging.MaximumLimit
                    NotePaging.DefaultLimit)
                false
            query
                QueryTaskId.CasePayments
                (caseTarget :: page)
                (paging
                    Paging.MaximumActorCursorCharacters
                    PaymentPaging.MaximumLimit
                    PaymentPaging.DefaultLimit)
                false
            query
                QueryTaskId.ReminderList
                ([
                    optional TaskInputId.ReminderView
                    optional (field BusinessField.CaseReference)
                    optional TaskInputId.ReminderState
                    optional TaskInputId.ReminderDue
                    optional TaskInputId.ReminderKindFilter
                    optional TaskInputId.DeadlineOutcomeFilter
                    optional TaskInputId.Assignment
                    optional (target InputTarget.Text)
                    optional TaskInputId.IncludeInactive
                 ]
                 @ page)
                (paging
                    Paging.MaximumCaseCursorCharacters
                    Paging.MaximumPageSize
                    Paging.DefaultPageSize)
                true
            query
                QueryTaskId.ReminderGet
                [ caseTarget; required (target InputTarget.ReminderId) ]
                None
                false
            query QueryTaskId.WorkViews [] None false
            query QueryTaskId.OperationGet [ required (target InputTarget.OperationId) ] None false
            query
                QueryTaskId.PeopleList
                ([
                    optional (target InputTarget.DirectorySearch)
                    optional TaskInputId.RoleFilter
                 ]
                 @ page)
                ordinaryPage
                false
            query QueryTaskId.PeopleGrants (required TaskInputId.ActorId :: page) ordinaryPage false
            query QueryTaskId.PeopleHistory (optional TaskInputId.ActorId :: page) ordinaryPage true
            query
                QueryTaskId.PeopleRequests
                page
                (paging
                    Paging.MaximumAccessRequestCursorCharacters
                    Paging.MaximumPageSize
                    Paging.DefaultPageSize)
                false
            query QueryTaskId.Context [] None false
        ]

    let commands =
        CommandTaskId.all
        |> List.map (fun task ->
            let inputs =
                match task with
                | CommandTaskId.DismissAccessRequest -> principal
                | CommandTaskId.TerminateOwnSessions -> []
                | CommandTaskId.TerminatePersonSessions -> [ required TaskInputId.ActorId ]

            {
                Id = CommandTaskId.key task
                Kind = EndpointKind.Command
                Inputs =
                    inputs
                    |> List.map (fun input ->
                        { input with
                            Source = InputSource.Authored
                        })
                Page = None
                Csv = false
            })

    let all = queries @ commands

    let private byId =
        all |> List.map (fun declaration -> declaration.Id, declaration) |> Map.ofList

    let find id = Map.tryFind id byId
