namespace ClaimCore.Web

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Antiforgery
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.RateLimiting
open Microsoft.Extensions.DependencyInjection

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

    let private caseEndpoints application json maximumBytes (runtime: IWebRuntime) =
        mapEndpoint
            application
            (WebContract.jsonPath "case.get")
            (Routes.get json maximumBytes runtime)

        mapEndpoint
            application
            (WebContract.jsonPath "case.list")
            (Routes.list json maximumBytes runtime)

        mapEndpoint
            application
            (WebContract.jsonPath "case.history")
            (Routes.history json maximumBytes runtime)

        mapEndpoint
            application
            (WebContract.jsonPath "operation.observe")
            (Routes.observe json maximumBytes runtime)

        mapEndpoint
            application
            (WebContract.jsonPath "command.prepare")
            (Routes.prepare json maximumBytes runtime)

        mapEndpoint
            application
            (WebContract.jsonPath "command.execute")
            (Routes.submit json maximumBytes runtime)

    let private recoveryEndpoints application json maximumBytes (runtime: IWebRuntime) =
        mapEndpoint
            application
            (WebContract.jsonPath "recovery.list")
            (Routes.recoveryList json maximumBytes runtime)

        mapEndpoint
            application
            (WebContract.jsonPath "recovery.inspect")
            (Routes.recoveryInspect json maximumBytes runtime)

        mapEndpoint
            application
            (WebContract.jsonPath "recovery.resolve")
            (Routes.recoveryResolve json maximumBytes runtime)

        mapEndpoint
            application
            (WebContract.jsonPath "recovery.dismiss")
            (Routes.recoveryDismiss json maximumBytes runtime)

        mapEndpoint
            application
            (WebContract.jsonPath "recovery.export")
            (Routes.recoveryExport json maximumBytes runtime)

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
        (runtime: IWebRuntime)
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
            (Routes.envelopePreview envelopePreview envelopeMaximum runtime)

        mapEndpoint
            application
            envelopeRetainPath
            (Routes.envelopeRetain
                envelopeRetain
                envelopeRetainMaximum
                (requiredHeader "recovery.importEnvelopeRetain")
                runtime)

        mapEndpoint
            application
            recordPreviewPath
            (Routes.recordPreview recordPreview recordMaximum runtime)

        mapEndpoint
            application
            recordRetainPath
            (Routes.recordRetain
                recordRetain
                recordRetainMaximum
                (requiredHeader "recovery.importRecordRetain")
                runtime)

    let map
        (configuration: WebConfiguration)
        (sessions: SessionRegistry)
        (runtime: IWebRuntime)
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

        caseEndpoints application json configuration.Admission.MaximumJsonBytes runtime
        recoveryEndpoints application json configuration.Admission.MaximumJsonBytes runtime

        importEndpoints
            application
            (rawAdmission "recovery.importEnvelopePreview")
            (rawAdmission "recovery.importEnvelopeRetain")
            (rawAdmission "recovery.importRecordPreview")
            (rawAdmission "recovery.importRecordRetain")
            runtime
