namespace ClaimCore.Web

open ClaimCore.Catalog

open ClaimCore.Wire

open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Hosting

module internal ApiRoutes =
    let handshake (context: HttpContext) =
        match context.Request.Headers.TryGetValue "ClaimCore-Contract" with
        | true, values when values.Count = 1 -> string values[0] = ConsumerContract.identity
        | _ -> false

    let ingress (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            match
                Endpoint.all
                |> List.tryFind (fun endpoint ->
                    Some(Endpoint.path endpoint) = Option.ofObj context.Request.Path.Value)
            with
            | Some endpoint when context.Request.Method <> Endpoint.method endpoint ->
                do!
                    (RouteSupport.hostFailure context HostFailure.MethodRejected).ExecuteAsync
                        context
            | _ -> do! next.Invoke context
        }

    let private admit (configuration: WebConfiguration) (context: HttpContext) =
        match configuration.Oidc with
        | None -> Task.FromResult(Error AdmissionFailure.SessionRejected)
        | Some oidc ->
            Admission.actorPost
                oidc
                configuration.Binding
                configuration.Admission.MaximumJsonBytes
                (context.RequestServices.GetRequiredService<IAntiforgery>())
                context

    let private input
        (endpoint: ClaimCore.Wire.Endpoint)
        (configuration: WebConfiguration)
        (context: HttpContext)
        =
        task {
            match! HttpBody.readRequest configuration.Admission.MaximumJsonBytes context with
            | Error problem -> return Error(RouteSupport.inputFailure context problem)
            | Ok bytes ->
                match
                    ClaimCore.Wire.Admission.admit endpoint bytes
                    |> Result.mapError (fun _ -> HostFailure.InputInvalid)
                    |> Result.bind ServiceGateway.admitTechnical
                with
                | Error reason -> return Error(RouteSupport.hostFailure context reason)
                | Ok _ when context.RequestAborted.IsCancellationRequested ->
                    return Error(RouteSupport.inputFailure context BodyProblem.BodyCancelled)
                | Ok input -> return Ok input
        }

    let handle endpoint configuration (service: IProtocolService) (context: HttpContext) =
        task {
            HttpHeaders.noStore context

            if not (handshake context) then
                return RouteSupport.hostFailure context HostFailure.ContractMismatch
            else
                match! admit configuration context with
                | Error reason -> return RouteSupport.admissionFailure context reason
                | Ok principal ->
                    match! input endpoint configuration context with
                    | Error result -> return result
                    | Ok input ->
                        WireRequest.operationId input
                        |> Option.iter (fun id -> RequestLog.operation id context)

                        RouteSupport.markDispatched context

                        let! outcome = service.Invoke(principal, input, context.RequestAborted)

                        RouteSupport.markCompleted context
                        return RouteSupport.reply outcome context
        }
