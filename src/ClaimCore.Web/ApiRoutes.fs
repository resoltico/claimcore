namespace ClaimCore.Web

open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Application
open ClaimCore.Contracts

module internal ApiRoutes =
    let handshake (context: HttpContext) =
        match context.Request.Headers.TryGetValue "ClaimCore-Contract" with
        | true, values when values.Count = 1 -> EndpointCatalog.matches (Some(string values[0]))
        | _ -> false

    let ingress (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            match
                EndpointCatalog.all
                |> List.tryFind (fun endpoint ->
                    Some endpoint.Path = Option.ofObj context.Request.Path.Value)
            with
            | Some endpoint when context.Request.Method <> endpoint.Method ->
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
        (endpoint: Endpoint)
        (configuration: WebConfiguration)
        (context: HttpContext)
        =
        task {
            match! HttpBody.readRequest configuration.Admission.MaximumJsonBytes context with
            | Error problem -> return Error(RouteSupport.inputFailure context problem)
            | Ok bytes ->
                match EndpointCatalog.admitRequest endpoint.Identifier bytes with
                | Error problem -> return Error(RouteSupport.inputFailure context problem)
                | Ok _ when context.RequestAborted.IsCancellationRequested ->
                    return Error(RouteSupport.inputFailure context HttpInputProblem.BodyCancelled)
                | Ok input -> return Ok input
        }

    let handle
        endpoint
        configuration
        (forActor: PrincipalKey -> IActorClaimsCore)
        (context: HttpContext)
        =
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
                        RouteSupport.markDispatched context

                        let! outcome =
                            ApiDispatch.invoke endpoint (forActor principal) input context

                        RouteSupport.markCompleted context
                        return RouteSupport.outcome endpoint.Identifier outcome context
        }
