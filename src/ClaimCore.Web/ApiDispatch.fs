namespace ClaimCore.Web

open System.Threading
open System.Threading.Tasks
open System.Text.Json.Nodes
open ClaimCore.Application
open ClaimCore.Contracts
open Microsoft.AspNetCore.Http

module internal ApiDispatch =
    let private execute (context: HttpContext) outcome =
        match outcome with
        | ExecuteOutcome.Accepted receipt -> RequestLog.actor receipt.RecordedBy context
        | _ -> ()

        OutcomeCodec.execute outcome

    let private command endpoint (core: IActorClaimsCore) draft (context: HttpContext) =
        task {
            match Drafts.bind draft with
            | Error error ->
                return
                    if endpoint = "command.preview" then
                        OutcomeCodec.preview (PreviewOutcome.Rejected(Rejection.Domain error))
                    else
                        OutcomeCodec.execute (ExecuteOutcome.Rejected(Rejection.Domain error))
            | Ok request ->
                if endpoint = "command.preview" then
                    let! outcome = core.Preview(request, context.RequestAborted)
                    return OutcomeCodec.preview outcome
                else
                    let! outcome = core.Execute(request, CancellationToken.None)
                    return execute context outcome
        }

    let private authority endpoint (core: IActorClaimsCore) input token =
        match endpoint, input with
        | "authority.register", ServiceInput.Register(id, principal) ->
            core.Authority.RegisterActor(id, principal, token)
        | "authority.setGrant", ServiceInput.SetGrant(id, principal, role, scope, active) ->
            core.Authority.SetGrant(id, principal, role, scope, active, token)
        | "authority.setEnabled", ServiceInput.SetEnabled(id, principal, enabled) ->
            core.Authority.SetEnabled(id, principal, enabled, token)
        | "authority.observe", ServiceInput.AuthorityObserve id -> core.Authority.Observe(id, token)
        | _ -> invalidArg "input" "The authority input does not match its endpoint."

    let private query endpoint (core: IActorClaimsCore) input token =
        task {
            match endpoint, input with
            | "definition", ServiceInput.Empty ->
                let! value = core.Definition token in return OutcomeCodec.definition value
            | "case.get", ServiceInput.CaseReference reference ->
                let! value = core.Get(reference, token) in return OutcomeCodec.currentCase value
            | "case.list", ServiceInput.CaseList request ->
                let! value = core.List(request, token) in return OutcomeCodec.caseList value
            | "case.history", ServiceInput.History request ->
                let! value = core.History(request, token) in return OutcomeCodec.history value
            | "operation.observe", ServiceInput.Operation id ->
                let! value = core.ObserveOperation(id, token) in return OutcomeCodec.operation value
            | _ -> return invalidArg "input" "The query input does not match its endpoint."
        }

    let identify input context =
        match input with
        | ServiceInput.Command value -> RequestLog.operation value.OperationId context
        | ServiceInput.Steward value -> RequestLog.operation value.OperationId context
        | ServiceInput.Operation id
        | ServiceInput.AuthorityObserve id
        | ServiceInput.Register(id, _)
        | ServiceInput.SetGrant(id, _, _, _, _)
        | ServiceInput.SetEnabled(id, _, _) -> RequestLog.operation id context
        | _ -> ()

    let invoke
        (endpoint: ClaimCore.Contracts.Endpoint)
        (core: IActorClaimsCore)
        input
        (context: HttpContext)
        : Task<JsonNode> =
        task {
            identify input context

            let token =
                if endpoint.Mutates then
                    CancellationToken.None
                else
                    context.RequestAborted

            match input with
            | ServiceInput.Command draft -> return! command endpoint.Identifier core draft context
            | ServiceInput.Steward request ->
                let! value = core.Steward(request, token) in return execute context value
            | ServiceInput.Register _
            | ServiceInput.SetGrant _
            | ServiceInput.SetEnabled _
            | ServiceInput.AuthorityObserve _ ->
                let! value = authority endpoint.Identifier core input token in
                return OutcomeCodec.authority value
            | _ -> return! query endpoint.Identifier core input token
        }
