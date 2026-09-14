namespace ClaimCore.Web

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.DependencyInjection
open ClaimCore.Application

/// Route registration keeps JSON, envelope, and canonical-record admission limits independent.
module ApiRoutes =
    let private mapEndpoint
        (application: WebApplication)
        (path: string)
        (handler: HttpContext -> Task<IResult>)
        =
        application
            .MapPost(path, Func<HttpContext, Task<IResult>>(handler))
            .RequireRateLimiting("core")
        |> ignore

    let private authenticated
        (configuration: WebConfiguration)
        (sessions: SessionRegistry)
        body
        limit
        (context: HttpContext)
        =
        let antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>()

        Admission.admitAuthenticated configuration.Origin body limit sessions antiforgery context

    let private caseEndpoints application json maximumBytes (core: IClaimsCore) =
        mapEndpoint
            application
            (WebContract.jsonPath "case.get")
            (Routes.get json maximumBytes core)

        mapEndpoint
            application
            (WebContract.jsonPath "case.list")
            (Routes.list json maximumBytes core)

        mapEndpoint
            application
            (WebContract.jsonPath "case.history")
            (Routes.history json maximumBytes core)

        mapEndpoint
            application
            (WebContract.jsonPath "operation.observe")
            (Routes.observe json maximumBytes core)

        mapEndpoint
            application
            (WebContract.jsonPath "command.prepare")
            (Routes.prepare json maximumBytes core)

        mapEndpoint
            application
            (WebContract.jsonPath "command.execute")
            (Routes.submit json maximumBytes core)

    let private recoveryEndpoints application json maximumBytes (core: IClaimsCore) =
        mapEndpoint
            application
            (WebContract.jsonPath "recovery.list")
            (Routes.recoveryList json maximumBytes core)

        mapEndpoint
            application
            (WebContract.jsonPath "recovery.inspect")
            (Routes.recoveryInspect json maximumBytes core)

        mapEndpoint
            application
            (WebContract.jsonPath "recovery.resolve")
            (Routes.recoveryResolve json maximumBytes core)

        mapEndpoint
            application
            (WebContract.jsonPath "recovery.dismiss")
            (Routes.recoveryDismiss json maximumBytes core)

        mapEndpoint
            application
            (WebContract.jsonPath "recovery.export")
            (Routes.recoveryExport json maximumBytes core)

    let private requiredHeader identifier =
        match WebContract.raw identifier with
        | _, _, _, [ header ] -> header
        | _ -> invalidOp $"The Web contract endpoint '{identifier}' has no one required header."

    let private importEndpoints
        application
        envelopePreview
        envelopeRetain
        recordPreview
        recordRetain
        (core: IClaimsCore)
        =
        let envelopePreviewPath, _, envelopeMaximum, _ =
            WebContract.raw "recovery.importEnvelopePreview"

        let envelopeRetainPath, _, envelopeRetainMaximum, _ =
            WebContract.raw "recovery.importEnvelopeRetain"

        let recordPreviewPath, _, recordMaximum, _ =
            WebContract.raw "recovery.importRecordPreview"

        let recordRetainPath, _, recordRetainMaximum, _ =
            WebContract.raw "recovery.importRecordRetain"

        mapEndpoint
            application
            envelopePreviewPath
            (Routes.envelopePreview envelopePreview envelopeMaximum core)

        mapEndpoint
            application
            envelopeRetainPath
            (Routes.envelopeRetain
                envelopeRetain
                envelopeRetainMaximum
                (requiredHeader "recovery.importEnvelopeRetain")
                core)

        mapEndpoint
            application
            recordPreviewPath
            (Routes.recordPreview recordPreview recordMaximum core)

        mapEndpoint
            application
            recordRetainPath
            (Routes.recordRetain
                recordRetain
                recordRetainMaximum
                (requiredHeader "recovery.importRecordRetain")
                core)

    let map
        (configuration: WebConfiguration)
        (sessions: SessionRegistry)
        (core: IClaimsCore)
        (application: WebApplication)
        =
        let json =
            authenticated
                configuration
                sessions
                RequestBody.Json
                configuration.Admission.MaximumJsonBytes

        let rawAdmission identifier =
            let _, mediaType, maximumBytes, _ = WebContract.raw identifier

            authenticated configuration sessions (RequestBody.Raw mediaType) maximumBytes

        caseEndpoints application json configuration.Admission.MaximumJsonBytes core
        recoveryEndpoints application json configuration.Admission.MaximumJsonBytes core

        importEndpoints
            application
            (rawAdmission "recovery.importEnvelopePreview")
            (rawAdmission "recovery.importEnvelopeRetain")
            (rawAdmission "recovery.importRecordPreview")
            (rawAdmission "recovery.importRecordRetain")
            core
