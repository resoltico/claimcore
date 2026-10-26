namespace ClaimCore.Web

open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Json
open ClaimCore.Application

open ClaimCore.Contract.Protocol

open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Web.Composition

module internal ApiRoutes =
    let handshake (context: HttpContext) =
        match context.Request.Headers.TryGetValue "ClaimCore-Contract" with
        | true, values when values.Count = 1 -> string values[0] = ContractIdentity.value
        | _ -> false

    let ingress (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            match
                Api.all
                |> List.tryFind (fun endpoint ->
                    Some(Api.path endpoint) = Option.ofObj context.Request.Path.Value)
            with
            | Some endpoint when context.Request.Method <> (Api.method endpoint).Method ->
                do!
                    (RouteSupport.hostFailure context HostFailureReason.MethodRejected).ExecuteAsync
                        context
            | _ -> do! next.Invoke context
        }

    let private admit (configuration: WebConfiguration) (context: HttpContext) =
        match configuration.Oidc with
        | None -> Task.FromResult(Error GateFailure.SessionRejected)
        | Some oidc ->
            RequestGate.actorPost
                oidc
                configuration.Binding
                configuration.RequestGate.MaximumJsonBytes
                (context.RequestServices.GetRequiredService<IAntiforgery>())
                context

    let private input
        (handler: ClaimCore.Application.Route)
        (configuration: WebConfiguration)
        (context: HttpContext)
        =
        task {
            match! HttpBody.readRequest configuration.RequestGate.MaximumJsonBytes context with
            | Error problem -> return Error(RouteSupport.inputFailure context problem)
            | Ok bytes ->
                match StrictJson.read configuration.RequestGate.MaximumJsonBytes bytes with
                | Error _ ->
                    return Error(RouteSupport.hostFailure context HostFailureReason.InputInvalid)
                | Ok value ->
                    match handler.Prepare value with
                    | Error _ ->
                        return
                            Error(RouteSupport.hostFailure context HostFailureReason.InputInvalid)
                    | Ok _ when context.RequestAborted.IsCancellationRequested ->
                        return Error(RouteSupport.inputFailure context BodyProblem.BodyCancelled)
                    | Ok prepared -> return Ok(value, prepared)
        }

    let handle handler configuration withActor (context: HttpContext) =
        let endpoint = handler.Description

        task {
            HttpHeaders.noStore context

            if not (handshake context) then
                return RouteSupport.hostFailure context HostFailureReason.ContractMismatch
            else
                match! admit configuration context with
                | Error reason -> return RouteSupport.gateFailure context reason
                | Ok principal ->
                    match! input handler configuration context with
                    | Error result -> return result
                    | Ok(input, prepared) ->
                        JsonValue.tryMember "operationId" input
                        |> Option.iter (function
                            | JsonValue.String text ->
                                RequestLog.operation (System.Guid.Parse text) context
                            | _ -> ())

                        RouteSupport.markDispatched context

                        let cancellation =
                            if Api.effect endpoint <> EndpointEffect.Read then
                                System.Threading.CancellationToken.None
                            else
                                context.RequestAborted

                        let! result =
                            withActor (AuthenticatedPrincipals.key principal) (fun core ->
                                prepared (Some core) cancellation (fun () ->
                                    RouteSupport.markCompleted context))

                        let! outcome =
                            task {
                                match result with
                                | Some reply -> return reply
                                | None ->
                                    RouteSupport.markCompleted context
                                    return! prepared None cancellation ignore
                            }

                        return RouteSupport.reply outcome context
        }
