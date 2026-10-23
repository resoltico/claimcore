namespace ClaimCore.Hosting

open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Contract
open ClaimCore.Contract.Protocol

/// Verified identity facts supplied by the HTTP authentication boundary.
[<StructuralEquality; NoComparison>]
type AuthenticatedSubject = private AuthenticatedSubject of PrincipalKey

module AuthenticatedSubjects =
    let human issuer subject =
        PrincipalKey.human issuer subject |> Result.map AuthenticatedSubject

    let service issuer subject =
        PrincipalKey.service issuer subject |> Result.map AuthenticatedSubject

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
                let! value = core.Get(request.CaseReference, token) in
                return OutcomeMapping.caseGet value
            | WireRequest.CaseList request ->
                let! value = core.List(request, token) in return OutcomeMapping.caseList value
            | WireRequest.CaseHistory request ->
                let! value = core.History(request, token) in return OutcomeMapping.caseHistory value
            | WireRequest.OperationObserve request ->
                let! value = core.Observe(request.OperationId, token) in
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
                let! value = core.Preview(request, token)
                return OutcomeMapping.preview request.OperationId value
            | WireRequest.CaseExecute request ->
                let! value = core.Execute(request, CancellationToken.None)

                return OutcomeMapping.execute request.OperationId value
            | WireRequest.AuthorityExecute request ->
                let! value = core.ExecuteAuthority(request, CancellationToken.None)
                return OutcomeMapping.authorityExecute request.OperationId value
            | WireRequest.Session
            | WireRequest.SessionLogout ->
                return invalidArg "request" "The HTTPS host owns session operations."
            | request -> return! reads core request token
        }

    let internal bind (forActor: PrincipalKey -> IActorClaimsCore) =
        { new IProtocolService with
            member _.Invoke(AuthenticatedSubject principal, request, cancellation) =
                dispatch (forActor principal) request cancellation
        }
