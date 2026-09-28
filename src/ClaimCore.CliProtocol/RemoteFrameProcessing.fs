namespace ClaimCore.Cli

open System
open System.IO
open System.Threading
open ClaimCore.Contracts

type private RemoteSessionHolder() =
    let mutable session: RemoteAccessSession option = None

    member _.Acquire() =
        match session with
        | Some value -> Ok value
        | None ->
            match RemoteConfiguration.fromEnvironment () with
            | Error _ -> Error CliRemoteProblem.Configuration
            | Ok configuration ->
                match RemoteAccessSession.Open configuration with
                | Error _ -> Error CliRemoteProblem.Authentication
                | Ok opened ->
                    session <- Some opened
                    Ok opened

    interface IDisposable with
        member _.Dispose() =
            session |> Option.iter (fun value -> (value :> IDisposable).Dispose())
            session <- None

module RemoteFrameProcessing =
    let private invoke
        (holder: RemoteSessionHolder)
        (delivery: FrameDelivery)
        stopped
        (bytes: byte array)
        =
        match StrictJson.parseDocument 131072 bytes with
        | Error problem -> CliRemoteWireCodec.protocolFailure 2 problem
        | Ok document ->
            use source = document

            match CliRemoteInvocation.decode source.RootElement with
            | Error problem -> CliRemoteWireCodec.protocolFailure 2 problem
            | Ok(identifier, input, timeout) ->
                delivery.BeforeAcquire()

                match holder.Acquire() with
                | Error reason -> CliRemoteWireCodec.localFailure identifier reason
                | Ok access ->
                    use deadline = new CancellationTokenSource()

                    use linked =
                        CancellationTokenSource.CreateLinkedTokenSource(stopped, deadline.Token)

                    timeout |> Option.iter deadline.CancelAfter

                    RemoteServiceCall.invoke
                        access
                        identifier
                        input
                        (fun () -> delivery.BeforeRemoteDispatch(identifier, input))
                        linked.Token
                    |> fun work -> work.GetAwaiter().GetResult()

    let private frame holder delivery stopped =
        function
        | InputFrame.Bytes bytes -> Some(invoke holder delivery stopped bytes)
        | InputFrame.Failure problem -> Some(CliRemoteWireCodec.protocolFailure 2 problem)
        | InputFrame.EndOfInput -> None

    let call (input: Stream) output errors stopped =
        let delivery = FrameDelivery(output, errors)
        use holder = new RemoteSessionHolder()

        try
            delivery.BeginFrame()

            match FrameReader.readDocument 131072 input |> frame holder delivery stopped with
            | None -> 0
            | Some response ->
                delivery.ObserveRendered()
                delivery.Write response
                response.ExitCode
        with error ->
            delivery.Failure error

    let session (input: Stream) output errors (stopped: CancellationToken) =
        let delivery = FrameDelivery(output, errors)
        use holder = new RemoteSessionHolder()

        try
            let mutable running = true

            while running && not stopped.IsCancellationRequested do
                delivery.BeginFrame()

                match FrameReader.readLine 131072 input |> frame holder delivery stopped with
                | None -> running <- false
                | Some response ->
                    delivery.ObserveRendered()
                    delivery.Write response

            if stopped.IsCancellationRequested then 130 else 0
        with error ->
            delivery.Failure error
