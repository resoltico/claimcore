namespace ClaimCore.Web

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Application

/// Every case-work request obtains a fresh verified principal and an actor-bound core. A request
/// never receives the unbound runtime facade or a database credential.
module ApiRoutes =
    let private admitted (_: HttpContext) = Task.FromResult(Ok())

    let private route
        (configuration: WebConfiguration)
        body
        maximumBytes
        (forActor: PrincipalKey -> IActorClaimsCore)
        invoke
        (context: HttpContext)
        =
        task {
            let antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>()

            match configuration.Oidc with
            | None -> return RouteSupport.admissionFailure context AdmissionFailure.SessionRejected
            | Some oidc ->
                match!
                    Admission.actorPost
                        oidc
                        configuration.Binding
                        body
                        maximumBytes
                        antiforgery
                        context
                with
                | Error failure -> return RouteSupport.admissionFailure context failure
                | Ok principal -> return! invoke (forActor principal) context
        }

    let private mapEndpoint (application: WebApplication) path handler =
        application
            .MapPost(path, Func<HttpContext, Task<IResult>>(handler))
            .RequireRateLimiting("core")
        |> ignore

    let private jsonHandlers maximum =
        [
            "case.get", Routes.get admitted maximum
            "case.list", Routes.list admitted maximum
            "case.history", Routes.history admitted maximum
            "operation.observe", Routes.observe admitted maximum
            "command.prepare", Routes.prepare admitted maximum
            "command.execute", Routes.submit admitted maximum
            "authority.register", Routes.managementRegister admitted maximum
            "authority.setGrant", Routes.managementSetGrant admitted maximum
            "authority.setEnabled", Routes.managementSetEnabled admitted maximum
            "authority.observe", Routes.managementObserve admitted maximum
            "lifecycle.review", LifecycleRoutes.review admitted maximum
            "lifecycle.apply", LifecycleRoutes.apply admitted maximum
            "lifecycle.approve", LifecycleRoutes.approve admitted maximum
            "tombstone.review", TombstoneRoutes.review admitted maximum
            "tombstone.approvePrune", TombstoneRoutes.approvePrune admitted maximum
            "tombstone.approveTerminal", TombstoneRoutes.approveTerminal admitted maximum
            "tombstone.changeHold", TombstoneRoutes.changeHold admitted maximum
            "authority.approveCopySigner", SignerApprovalRoutes.approve admitted maximum
            "authority.approveCopyDeletion", CopyDeletionApprovalRoutes.approve admitted maximum
            "authority.approveCopyAdoption", CopyAdoptionApprovalRoutes.approve admitted maximum
            "authority.approveWriterHandoff", WriterHandoffApprovalRoutes.approve admitted maximum
            "authority.reviewRealDataActivation", RealDataActivationRoutes.review admitted maximum
            "authority.approveRealDataActivation", RealDataActivationRoutes.approve admitted maximum
            "recovery.list", Routes.recoveryList admitted maximum
            "recovery.inspect", Routes.recoveryInspect admitted maximum
            "recovery.resolve", Routes.recoveryResolve admitted maximum
            "recovery.dismiss", Routes.recoveryDismiss admitted maximum
            "recovery.export", Routes.recoveryExport admitted maximum
        ]

    let private jsonEndpoints application configuration forActor =
        let maximum = configuration.Admission.MaximumJsonBytes
        let json = route configuration RequestBody.Json maximum forActor

        for identifier, handler in jsonHandlers maximum do
            mapEndpoint application (WebContract.jsonPath identifier) (json handler)

    let private requiredHeader identifier =
        match WebContract.raw identifier with
        | _, _, _, [ header ] -> header
        | _ -> invalidOp $"The Web contract endpoint '{identifier}' has no one required header."

    let private importEndpoints application configuration forActor =
        let previewPath, previewMedia, previewMaximum, _ =
            WebContract.raw "recovery.importEnvelopePreview"

        let retainPath, retainMedia, retainMaximum, _ =
            WebContract.raw "recovery.importEnvelopeRetain"

        let preview =
            route configuration (RequestBody.Raw previewMedia) previewMaximum forActor

        let retain =
            route configuration (RequestBody.Raw retainMedia) retainMaximum forActor

        mapEndpoint
            application
            previewPath
            (preview (Routes.envelopePreview admitted previewMaximum))

        mapEndpoint
            application
            retainPath
            (retain (
                Routes.envelopeRetain
                    admitted
                    retainMaximum
                    (requiredHeader "recovery.importEnvelopeRetain")
            ))

    let map configuration forActor (application: WebApplication) =
        jsonEndpoints application configuration forActor
        importEndpoints application configuration forActor
