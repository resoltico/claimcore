namespace ClaimCore.Cli

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Hosting

type RuntimeSession() =
    let mutable runtime: Runtime option = None

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

    member _.Run(endpoint, input, timeout: int option, stopped: CancellationToken) =
        task {
            use cancellation = new CancellationTokenSource()

            use linked =
                CancellationTokenSource.CreateLinkedTokenSource(stopped, cancellation.Token)

            match timeout with
            | Some milliseconds -> cancellation.CancelAfter(milliseconds)
            | None -> ()

            match runtime with
            | Some active ->
                return! EndpointDispatch.execute active.Core endpoint input linked.Token
            | None ->
                match PrivateFiles.connection () with
                | Error message ->
                    return
                        JsonResponse.protocolFailure
                            3
                            (ProtocolFailure.create "CONFIGURATION_ERROR" message "")
                | Ok connection ->
                    match! Runtime.OpenPostgres(connection, linked.Token) with
                    | Error fault -> return openFault endpoint fault
                    | Ok opened ->
                        runtime <- Some opened
                        return! EndpointDispatch.execute opened.Core endpoint input linked.Token
        }

    interface IDisposable with
        member _.Dispose() =
            runtime |> Option.iter (fun active -> (active :> IDisposable).Dispose())
            runtime <- None

module CliRunner =
    let invoke (session: RuntimeSession) stopped bytes =
        match StrictJson.parseDocument 131072 bytes with
        | Error problem -> Task.FromResult(JsonResponse.protocolFailure 2 problem)
        | Ok document ->
            use source = document

            match InvocationFraming.decode source.RootElement with
            | Error problem -> Task.FromResult(JsonResponse.protocolFailure 2 problem)
            | Ok(endpoint, input, timeout) -> session.Run(endpoint, input, timeout, stopped)
