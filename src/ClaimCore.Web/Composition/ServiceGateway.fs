namespace ClaimCore.Web.Composition

open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Web
open ClaimCore.Contract
open ClaimCore.Contract.Protocol

type IProtocolService =
    abstract Invoke: AuthenticatedSubject * WireRequest * CancellationToken -> Task<WireReply>

module ServiceGateway =
    let admitTechnical request = Ok request

    let private reads (core: IActorClaimsCore) input token =
        task {
            match input with
            | WireRequest.Context ->
                let! value = core.Context token in return OutcomeMapping.context value
            | WireRequest.CaseGet request ->
                let! value = core.Get(request, token) in return OutcomeMapping.caseGet value
            | WireRequest.CaseList request ->
                let! value = core.List(request, token) in return OutcomeMapping.caseList value
            | WireRequest.CaseHistory request ->
                let! value = core.History(request, token) in return OutcomeMapping.caseHistory value
            | WireRequest.OperationObserve request ->
                let! value = core.Observe(request, token) in
                return OutcomeMapping.operationObserve value
            | WireRequest.AuthorityList request ->
                let! value = core.ListActors(request, token)
                return OutcomeMapping.authorityList value
            | WireRequest.AuthorityGrants request ->
                let! value = core.ListGrants(request, token)
                return OutcomeMapping.authorityGrants value
            | _ -> return invalidArg "request" "Expected an application query."
        }

    let private dispatch (core: IActorClaimsCore) input token =
        task {
            match input with
            | WireRequest.CasePreview request ->
                let! value = core.Preview(OperationInput.Case request, token)
                return OutcomeMapping.preview request.OperationId value.Outcome
            | WireRequest.CaseExecute request ->
                let! value = core.Execute(OperationInput.Case request, CancellationToken.None)

                return OutcomeMapping.execute request.OperationId value.Outcome
            | WireRequest.AuthorityExecute request ->
                let! value = core.Execute(OperationInput.Authority request, CancellationToken.None)
                return OutcomeMapping.authorityExecute request.OperationId value.Outcome
            | WireRequest.Session
            | WireRequest.SessionLogout ->
                return invalidArg "request" "The HTTPS host owns session operations."
            | request -> return! reads core request token
        }

    let internal bind (forActor: PrincipalKey -> IActorClaimsCore) =
        { new IProtocolService with
            member _.Invoke(subject, request, cancellation) =
                dispatch (forActor (AuthenticatedSubjects.key subject)) request cancellation
        }
