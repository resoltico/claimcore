module ClaimCore.Cli.Program

open System
open System.IO
open System.Text
open System.Threading
open ClaimCore.Application
open ClaimCore.Cli

[<NoEquality; NoComparison>]
type private DeliveryState =
    {
        mutable RecoveryIdentity: (Guid * string option) option
    }

let private writeBytes (bytes: byte array) =
    let output = Console.OpenStandardOutput()
    output.Write(bytes, 0, bytes.Length)
    output.Flush()

let private writeDiscovery (response: DiscoveryResponse) =
    match response with
    | DiscoveryResponse.Text text -> Console.Out.WriteLine(text)
    | DiscoveryResponse.Json bytes -> writeBytes bytes

let private identity (endpoint: Endpoint) (input: EndpointInput) =
    match endpoint, input with
    | Endpoint.CommandPrepare, EndpointInput.Draft draft
    | Endpoint.CommandExecute, EndpointInput.Draft draft -> Some(draft.OperationId, None)
    | Endpoint.RecoveryResolve, EndpointInput.RecoveryResolve(operationId, digest)
    | Endpoint.RecoveryDismiss, EndpointInput.RecoveryDismiss(operationId, digest) ->
        Some(operationId, Some digest)
    | _ -> None

let private invoke
    (state: DeliveryState)
    (session: RuntimeSession)
    (stopped: CancellationToken)
    (bytes: byte array)
    : RenderedResponse =
    match StrictJson.parseDocument 131072 bytes with
    | Error problem -> JsonResponse.protocolFailure 2 problem
    | Ok document ->
        use source = document

        match InvocationFraming.decode source.RootElement with
        | Error problem -> JsonResponse.protocolFailure 2 problem
        | Ok(endpoint, input, timeout) ->
            state.RecoveryIdentity <- identity endpoint input
            session.Run(endpoint, input, timeout, stopped).GetAwaiter().GetResult()

let private call (state: DeliveryState) (stopped: CancellationToken) =
    use session = new RuntimeSession()
    let frame = FrameReader.readDocument 131072 (Console.OpenStandardInput())

    match frame with
    | InputFrame.EndOfInput -> 0
    | InputFrame.Failure problem ->
        JsonResponse.protocolFailure 2 problem |> JsonResponse.bytes |> writeBytes
        2
    | InputFrame.Bytes bytes ->
        let result = invoke state session stopped bytes
        JsonResponse.bytes result |> writeBytes
        result.ExitCode

let private session (state: DeliveryState) (stopped: CancellationToken) =
    use runtime = new RuntimeSession()
    let input = Console.OpenStandardInput()
    let mutable running = true

    while running && not stopped.IsCancellationRequested do
        match FrameReader.readLine 131072 input with
        | InputFrame.EndOfInput -> running <- false
        | InputFrame.Failure problem ->
            JsonResponse.protocolFailure 2 problem |> JsonResponse.bytes |> writeBytes
        | InputFrame.Bytes bytes ->
            let result = invoke state runtime stopped bytes
            JsonResponse.bytes result |> writeBytes

    0

let private unsupported () =
    Console.Error.WriteLine("Unsupported invocation. Run 'claimcore help' for the CLI v3 grammar.")
    Console.Error.Flush()
    64

let private discovery arguments =
    match arguments with
    | []
    | [ "help" ] ->
        Discovery.help () |> writeDiscovery
        0
    | "help" :: _ ->
        Discovery.help () |> writeDiscovery
        0
    | [ "version" ] ->
        Console.Out.WriteLine(BuildIdentity.current.Version)
        Console.Out.Flush()
        0
    | [ "version"; "--json" ] ->
        Discovery.versionJson () |> writeBytes
        0
    | "describe" :: rest ->
        match Discovery.describe rest with
        | Ok response ->
            writeDiscovery response
            0
        | Error _ -> unsupported ()
    | "schema" :: rest ->
        match Discovery.schema rest with
        | Ok response ->
            writeDiscovery response
            0
        | Error _ -> unsupported ()
    | _ -> unsupported ()

let private dispatch (state: DeliveryState) (stopped: CancellationToken) (arguments: string list) =
    match arguments with
    | [ "call" ] -> call state stopped
    | [ "session" ] -> session state stopped
    | _ -> discovery arguments

let private deliveryFailure (state: DeliveryState) (message: string) =
    Console.Error.WriteLine(message)

    state.RecoveryIdentity
    |> Option.iter (fun (operationId, digest) ->
        Console.Error.WriteLine("Operation ID: " + operationId.ToString("D"))

        digest
        |> Option.iter (fun value -> Console.Error.WriteLine("Request SHA-256: " + value)))

    Console.Error.Flush()

[<EntryPoint>]
let main argv =
    let state = { RecoveryIdentity = None }
    use stopped = new CancellationTokenSource()

    Console.CancelKeyPress.Add(fun event ->
        event.Cancel <- true
        stopped.Cancel())

    try
        BuildIdentity.requireCompatibleAssembly typeof<Endpoint>.Assembly
        Console.OutputEncoding <- UTF8Encoding(false)
        dispatch state stopped.Token (Array.toList argv)
    with
    | :? IOException when state.RecoveryIdentity.IsSome ->
        deliveryFailure
            state
            "Result delivery failed after an operation was admitted. Recover or observe the exact operation; do not create a replacement operation."

        4
    | :? IOException ->
        deliveryFailure state "Result delivery failed before a mutation was admitted."
        3
    | error ->
        deliveryFailure state ("Unexpected internal failure: " + error.GetType().Name)
        if state.RecoveryIdentity.IsSome then 4 else 70
