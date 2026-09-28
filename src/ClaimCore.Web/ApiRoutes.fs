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
                        configuration.Origin
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

    let private caseEndpoints application configuration forActor =
        let maximum = configuration.Admission.MaximumJsonBytes
        let json = route configuration RequestBody.Json maximum forActor

        mapEndpoint
            application
            (WebContract.jsonPath "case.get")
            (json (Routes.get admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "case.list")
            (json (Routes.list admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "case.history")
            (json (Routes.history admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "operation.observe")
            (json (Routes.observe admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "command.prepare")
            (json (Routes.prepare admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "command.execute")
            (json (Routes.submit admitted maximum))

    let private recoveryEndpoints application configuration forActor =
        let maximum = configuration.Admission.MaximumJsonBytes
        let json = route configuration RequestBody.Json maximum forActor

        mapEndpoint
            application
            (WebContract.jsonPath "recovery.list")
            (json (Routes.recoveryList admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "recovery.inspect")
            (json (Routes.recoveryInspect admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "recovery.resolve")
            (json (Routes.recoveryResolve admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "recovery.dismiss")
            (json (Routes.recoveryDismiss admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "recovery.export")
            (json (Routes.recoveryExport admitted maximum))

    let private managementEndpoints application configuration forActor =
        let maximum = configuration.Admission.MaximumJsonBytes
        let json = route configuration RequestBody.Json maximum forActor

        mapEndpoint
            application
            (WebContract.jsonPath "authority.register")
            (json (Routes.managementRegister admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "authority.setGrant")
            (json (Routes.managementSetGrant admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "authority.setEnabled")
            (json (Routes.managementSetEnabled admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "authority.observe")
            (json (Routes.managementObserve admitted maximum))

    let private lifecycleEndpoints application configuration forActor =
        let maximum = configuration.Admission.MaximumJsonBytes
        let json = route configuration RequestBody.Json maximum forActor

        mapEndpoint
            application
            (WebContract.jsonPath "lifecycle.review")
            (json (LifecycleRoutes.review admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "lifecycle.apply")
            (json (LifecycleRoutes.apply admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "lifecycle.approve")
            (json (LifecycleRoutes.approve admitted maximum))

    let private tombstoneEndpoints application configuration forActor =
        let maximum = configuration.Admission.MaximumJsonBytes
        let json = route configuration RequestBody.Json maximum forActor

        mapEndpoint
            application
            (WebContract.jsonPath "tombstone.review")
            (json (TombstoneRoutes.review admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "tombstone.approvePrune")
            (json (TombstoneRoutes.approvePrune admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "tombstone.approveTerminal")
            (json (TombstoneRoutes.approveTerminal admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "tombstone.changeHold")
            (json (TombstoneRoutes.changeHold admitted maximum))

    let private signerApprovalEndpoint application configuration forActor =
        let maximum = configuration.Admission.MaximumJsonBytes
        let json = route configuration RequestBody.Json maximum forActor

        mapEndpoint
            application
            (WebContract.jsonPath "authority.approveCopySigner")
            (json (SignerApprovalRoutes.approve admitted maximum))

    let private copyDeletionApprovalEndpoint application configuration forActor =
        let maximum = configuration.Admission.MaximumJsonBytes
        let json = route configuration RequestBody.Json maximum forActor

        mapEndpoint
            application
            (WebContract.jsonPath "authority.approveCopyDeletion")
            (json (CopyDeletionApprovalRoutes.approve admitted maximum))

    let private copyAdoptionApprovalEndpoint application configuration forActor =
        let maximum = configuration.Admission.MaximumJsonBytes
        let json = route configuration RequestBody.Json maximum forActor

        mapEndpoint
            application
            (WebContract.jsonPath "authority.approveCopyAdoption")
            (json (CopyAdoptionApprovalRoutes.approve admitted maximum))

    let private writerHandoffApprovalEndpoint application configuration forActor =
        let maximum = configuration.Admission.MaximumJsonBytes
        let json = route configuration RequestBody.Json maximum forActor

        mapEndpoint
            application
            (WebContract.jsonPath "authority.approveWriterHandoff")
            (json (WriterHandoffApprovalRoutes.approve admitted maximum))

    let private realDataActivationEndpoints application configuration forActor =
        let maximum = configuration.Admission.MaximumJsonBytes
        let json = route configuration RequestBody.Json maximum forActor

        mapEndpoint
            application
            (WebContract.jsonPath "authority.reviewRealDataActivation")
            (json (RealDataActivationRoutes.review admitted maximum))

        mapEndpoint
            application
            (WebContract.jsonPath "authority.approveRealDataActivation")
            (json (RealDataActivationRoutes.approve admitted maximum))

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
        caseEndpoints application configuration forActor
        managementEndpoints application configuration forActor
        lifecycleEndpoints application configuration forActor
        tombstoneEndpoints application configuration forActor
        signerApprovalEndpoint application configuration forActor
        copyDeletionApprovalEndpoint application configuration forActor
        copyAdoptionApprovalEndpoint application configuration forActor
        writerHandoffApprovalEndpoint application configuration forActor
        realDataActivationEndpoints application configuration forActor
        recoveryEndpoints application configuration forActor
        importEndpoints application configuration forActor
