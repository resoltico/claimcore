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

    let identify input context =
        match input with
        | ServiceInput.Case value -> RequestLog.operation value.OperationId context
        | ServiceInput.Authority value -> RequestLog.operation value.OperationId context
        | ServiceInput.Operation id -> RequestLog.operation id context
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

            match endpoint.Identifier, input with
            | "context", ServiceInput.Empty ->
                let! value = core.Context token in return OutcomeCodec.context value
            | "case.get", ServiceInput.CaseReference reference ->
                let! value = core.Get(reference, token) in return OutcomeCodec.currentCase value
            | "case.list", ServiceInput.CaseList request ->
                let! value = core.List(request, token) in return OutcomeCodec.caseList value
            | "case.history", ServiceInput.History request ->
                let! value = core.History(request, token) in return OutcomeCodec.history value
            | "operation.observe", ServiceInput.Operation id ->
                let! value = core.Observe(id, token) in return OutcomeCodec.operation value
            | "authority.list", ServiceInput.ActorList request ->
                let! value = core.ListActors(request, token) in return OutcomeCodec.actors value
            | "case.preview", ServiceInput.Case request ->
                let! value = core.Preview(request, token) in return OutcomeCodec.preview value
            | "case.execute", ServiceInput.Case request ->
                let! value = core.Execute(request, token) in return execute context value
            | "authority.execute", ServiceInput.Authority request ->
                let! value = core.ExecuteAuthority(request, token) in return execute context value
            | _ -> return invalidArg "input" "The admitted input does not match its endpoint."
        }
