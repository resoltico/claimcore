namespace ClaimCore.Cli

open System
open System.Threading
open ClaimCore.Application
open ClaimCore.Contracts

/// Applies one decoded invocation to the supplied core under the invocation's own deadline. It
/// holds no runtime, no store, and no connection: the composition root supplies the core.
type InvocationSession(supplier: ICoreSupplier) =
    let openFault endpoint fault =
        let code, message =
            match fault with
            | RuntimeOpenFault.RuntimeConfigurationInvalid ->
                FaultCode.SchemaMismatch, "The local runtime configuration is invalid."
            | RuntimeOpenFault.RuntimeSchemaMismatch ->
                FaultCode.SchemaMismatch,
                "The PostgreSQL schema is not compatible with this runtime."
            | RuntimeOpenFault.RuntimeStoreUnavailable ->
                FaultCode.StoreUnavailable, "The PostgreSQL runtime is unavailable."
            | RuntimeOpenFault.RuntimeStoreIntegrityError ->
                FaultCode.StoreIntegrityError, "The PostgreSQL runtime failed integrity checks."
            | RuntimeOpenFault.RuntimeCancelled ->
                FaultCode.StoreUnavailable,
                "Runtime opening was cancelled before work was admitted."

        CliWireCodec.localFailure
            (Endpoint.identifier endpoint)
            {
                Code = code
                Message = message
                Action = RecommendedAction.StopAndInvestigate
            }

    let unavailable endpoint reason =
        match reason with
        | CoreUnavailable.Configuration message ->
            JsonResponse.protocolFailure 3 (ProtocolFailure.create "CONFIGURATION_ERROR" message "")
        | CoreUnavailable.Open fault -> openFault endpoint fault

    member _.Run(endpoint, input, timeout: int option, stopped: CancellationToken) =
        task {
            use cancellation = new CancellationTokenSource()

            use linked =
                CancellationTokenSource.CreateLinkedTokenSource(stopped, cancellation.Token)

            match timeout with
            | Some milliseconds -> cancellation.CancelAfter(milliseconds)
            | None -> ()

            match! supplier.Acquire linked.Token with
            | Error reason -> return unavailable endpoint reason
            | Ok core -> return! EndpointDispatch.execute core endpoint input linked.Token
        }
