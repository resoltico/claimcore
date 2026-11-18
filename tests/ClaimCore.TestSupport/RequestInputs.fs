module ClaimCore.TestSupport.RequestInputs

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire

let all () : IEndpointInput list =
    let targetId = Guid.Parse "10000000-0000-7000-8000-000000000001"
    let target = CaseSelector.Reference "CASE-1"
    let page: PageRequest = { AfterCursor = None; Limit = 50 }

    [
        yield EndpointValues.input Api.session ()
        yield EndpointValues.input Api.logout ()
        yield EndpointValues.input Api.context ()
        yield EndpointValues.input Api.caseGet { Target = target }
        yield
            EndpointValues.input
                Api.caseNotes
                {
                    Target = target
                    AfterCursor = None
                    Limit = 20
                }
        yield
            EndpointValues.input
                Api.casePayments
                {
                    Target = target
                    AfterCursor = None
                    Limit = 20
                }
        yield
            EndpointValues.input
                Api.caseList
                {
                    Page = page
                    Filter = CaseListFilter.empty
                }
        yield
            EndpointValues.input Api.caseSummary (CaseSummaryRequest.ungrouped CaseListFilter.empty)
        yield
            EndpointValues.input
                Api.caseAssignees
                {
                    Target = target
                    AfterCursor = None
                    Limit = 50
                }
        yield
            EndpointValues.input
                Api.caseTimeline
                {
                    Target = target
                    Filter = None
                    AfterCursor = None
                    Limit = 50
                }
        yield EndpointValues.input Api.workViews ()
        yield
            EndpointValues.input
                Api.reminder
                {
                    Target = target
                    ReminderId = targetId
                }
        yield
            EndpointValues.input
                Api.reminders
                {
                    Target = None
                    View = None
                    State = None
                    Kind = None
                    DeadlineOutcome = None
                    Due = None
                    Assignment = None
                    Text = None
                    AfterCursor = None
                    Limit = 50
                    IncludeInactive = None
                }
        yield
            EndpointValues.input
                Api.peopleList
                {
                    Role = None
                    Text = None
                    AfterCursor = None
                    Limit = 50
                }
        yield
            EndpointValues.input
                Api.peopleGrants
                {
                    ActorId = targetId
                    AfterCursor = None
                    Limit = 50
                }
        yield
            EndpointValues.input
                Api.peopleHistory
                {
                    TargetActorId = None
                    AfterCursor = None
                    Limit = 50
                }
        yield EndpointValues.input Api.peopleRequests page
        yield EndpointValues.input Api.peopleRequestsDismiss DomainInputs.principal
        yield EndpointValues.input Api.terminateOwnSessions ()
        yield EndpointValues.input Api.terminatePersonSessions { ActorId = targetId }
        yield EndpointValues.input Api.operationGet { OperationId = targetId }
        for preview in [ true; false ] do
            let bind value =
                if preview then
                    EndpointValues.input Api.operationPreview value
                else
                    EndpointValues.input Api.operationExecute value

            let create task binding values =
                Submissions.create task (OperationIds.fresh ()) binding values |> bind

            yield
                create
                    "case.open"
                    (Binding.CaseNew Revision.zero)
                    (Submissions.fields (RegistrationInput.values DomainInputs.registration)
                     |> Map.add
                         (Submissions.field BusinessField.CaseReference)
                         (SubmissionValue.Text "CASE-1"))

            yield
                create
                    "note.add"
                    (Binding.NoteNew target)
                    (Map.ofList
                        [
                            Submissions.input InputTarget.NoteText,
                            SubmissionValue.Text "Synthetic note"
                        ])

            yield
                create
                    "note.remove"
                    (Binding.Note(target, NoteId.create targetId |> Option.get))
                    Map.empty

            yield
                create
                    "people.register"
                    (Binding.PeopleNew Revision.zero)
                    (Submissions.principal (
                        PrincipalKey.human "https://issuer.example/" "synthetic"
                        |> Result.toOption
                        |> Option.get
                     )
                     |> Map.fold
                         (fun values key value -> Map.add key value values)
                         (Submissions.profile DomainInputs.actorProfile))

            yield
                create
                    "work.update"
                    (Binding.Work(target, Revision.zero))
                    (Map.ofList
                        [
                            Submissions.field BusinessField.HandlingSummary,
                            SubmissionValue.Text "Follow up"
                        ])

            yield
                create
                    "reminder.create"
                    (Binding.ReminderNew target)
                    (Map.ofList
                        [
                            Submissions.input InputTarget.ReminderAction,
                            SubmissionValue.Text "Contact claimant"
                            Submissions.input InputTarget.ReminderDueDate,
                            SubmissionValue.Text "2026-09-01"
                        ])

    ]

let values () =
    all ()
    |> List.map (fun input ->
        input.Accept
            { new IEndpointInputVisitor<Route * ClaimCore.Json.JsonValue> with
                member _.Visit<'request, 'reply>(endpoint: Endpoint<'request, 'reply>, value) =
                    Route.describe endpoint, endpoint.Input.Write value
            })

let forEndpoint endpoint =
    values () |> List.find (fun (route, _) -> route.Token = endpoint) |> snd
