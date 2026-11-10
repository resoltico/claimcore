module ClaimCore.TestSupport.RequestInputs

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire

let all () : IEndpointInput list =
    let operation = OperationIds.fresh ()
    let targetId = Guid.Parse "10000000-0000-7000-8000-000000000001"
    let target = CaseSelector.Reference "CASE-1"
    let page: PageRequest = { AfterCursor = None; Limit = 50 }

    let caseInput =
        Inputs.caseOperation
            {
                OperationId = operation
                CaseReference = "CASE-1"
                ExpectedRevision = 0L
                Act = AuthoredCaseAct.Open Inputs.registration
            }

    let principal =
        {
            PrincipalInput.Kind = PrincipalKind.Human
            Issuer = "https://issuer.example/"
            Subject = "synthetic"
        }

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
        yield EndpointValues.input Api.caseSummary CaseListFilter.empty
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
                    Due = None
                    Assignment = None
                    Text = None
                    AfterCursor = None
                    Limit = 50
                    IncludeInactive = None
                }
        yield
            EndpointValues.input
                Api.authorityList
                {
                    Role = None
                    Text = None
                    AfterCursor = None
                    Limit = 50
                }
        yield
            EndpointValues.input
                Api.authorityGrants
                {
                    ActorId = targetId
                    AfterCursor = None
                    Limit = 50
                }
        yield
            EndpointValues.input
                Api.authorityHistory
                {
                    TargetActorId = None
                    AfterCursor = None
                    Limit = 50
                }
        yield EndpointValues.input Api.authorityRequests page
        yield EndpointValues.input Api.authorityRequestsDismiss principal
        yield EndpointValues.input Api.operationGet { OperationId = targetId }
        for preview in [ true; false ] do
            // Both operation endpoints consume the same authored input type.
            let bind value =
                let fresh = OperationIds.fresh ()

                let value =
                    match value with
                    | OperationInput.Case input ->
                        OperationInput.Case { input with OperationId = fresh }
                    | OperationInput.Authority input ->
                        OperationInput.Authority { input with OperationId = fresh }
                    | OperationInput.Note input ->
                        OperationInput.Note { input with OperationId = fresh }
                    | OperationInput.Work input ->
                        OperationInput.Work { input with OperationId = fresh }
                    | OperationInput.Reminder input ->
                        OperationInput.Reminder { input with OperationId = fresh }

                if preview then
                    EndpointValues.input Api.operationPreview value
                else
                    EndpointValues.input Api.operationExecute value

            yield
                bind (
                    OperationInput.Note
                        {
                            OperationId = operation
                            Target = target
                            Act = AuthoredNoteAct.AddNote "Authored note"
                        }
                )

            yield
                bind (
                    OperationInput.Note
                        {
                            OperationId = operation
                            Target = target
                            Act = AuthoredNoteAct.RemoveNote targetId
                        }
                )

            yield bind caseInput

            yield
                bind (
                    OperationInput.Authority
                        {
                            OperationId = operation
                            Target = principal
                            Act = AuthorityAct.Register Inputs.actorProfile
                        }
                )

            yield
                bind (
                    OperationInput.Work
                        {
                            OperationId = operation
                            Target = target
                            ExpectedRevision = 0L
                            Act =
                                WorkAct.UpdateWork
                                    {
                                        Responsible = WorkValue.Keep
                                        Summary = WorkValue.Set "Follow up"
                                    }
                        }
                )

            yield
                bind (
                    OperationInput.Reminder
                        {
                            OperationId = operation
                            Target = target
                            Act =
                                AuthoredReminderAct.Create
                                    {
                                        Action = "Contact claimant"
                                        Due = "2026-09-01"
                                    }
                        }
                )
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
