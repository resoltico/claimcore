module ClaimCore.WebTests.ReadCancellationTests

open System
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Expecto
open ClaimCore.Application
open ClaimCore.Contracts
open ClaimCore.Web
open ClaimCore.WebTests.RouteFixtures

/// Captures actual adapter arguments without executing business or storage decisions.
type TokenCaptureProxy() =
    inherit DispatchProxy()
    member val Tokens = ResizeArray<CancellationToken>() with get, set

    override this.Invoke(method, arguments) : obj | null =
        let method =
            method
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failtest "Proxy needs a method")

        let arguments = arguments |> Option.ofObj |> Option.defaultValue [||]

        if method.Name.StartsWith("get_", StringComparison.Ordinal) then
            let child = DispatchProxy.Create(method.ReturnType, typeof<TokenCaptureProxy>)
            (unbox<TokenCaptureProxy> child).Tokens <- this.Tokens
            child
        else
            arguments
            |> Array.tryPick (function
                | :? CancellationToken as token -> Some token
                | _ -> None)
            |> Option.iter this.Tokens.Add

            let result = method.ReturnType.GetGenericArguments()[0]

            typeof<Task>.GetMethods()
            |> Array.find (fun candidate ->
                candidate.Name = "FromException" && candidate.IsGenericMethod)
            |> fun factory ->
                factory
                    .MakeGenericMethod(result)
                    .Invoke(null, [| box (IOException("synthetic token probe")) |])

let private proxy<'service when 'service: not struct and 'service: not null> () =
    let value = DispatchProxy.Create(typeof<'service>, typeof<TokenCaptureProxy>)

    unbox<'service> value, unbox<TokenCaptureProxy> value

let private binding (identifier: string) handler =
    testCase
        ($"[CC-WEB-001] reviewed endpoint {identifier} propagates its classified token")
        (fun () ->
            let core, capture = proxy<IActorClaimsCore>()
            use caller = new CancellationTokenSource()

            let request =
                context (
                    if identifier = "recovery.importEnvelopePreview" then
                        "{}"
                    else
                        TestServerRouteInputs.input identifier
                )

            request.RequestAborted <- caller.Token

            try
                handler core request |> readResult |> ignore
            with _ ->
                ()

            let captured = capture.Tokens |> Seq.toList

            Expect.equal captured.Length 1 "The decoded request reaches exactly one native member."

            let expected =
                if CliMutationCatalog.isMutation identifier then
                    CancellationToken.None
                else
                    caller.Token

            Expect.equal
                captured.Head
                expected
                "Contracts classification owns the delivery policy.")

let tests =
    let _, _, rawMaximum, _ = WebContract.raw "recovery.importEnvelopePreview"

    let handlers =
        ("recovery.importEnvelopePreview", Routes.envelopePreview admit rawMaximum)
        :: ApiRoutes.jsonHandlers 65536
        |> Map.ofList

    (ContractProjection.current ()).WebEndpoints
    |> List.filter (fun endpoint ->
        not (CliMutationCatalog.isMutation endpoint.Identifier)
        || endpoint.Identifier = "command.execute"
        || endpoint.Identifier = "authority.approveRealDataActivation")
    |> List.map (fun endpoint ->
        match handlers.TryFind endpoint.Identifier with
        | Some handler -> binding endpoint.Identifier handler
        | None ->
            testCase
                ($"[CC-WEB-001] reviewed endpoint {endpoint.Identifier} has a token binding")
                (fun () -> failtest "Every reviewed read-only contract requires a binding probe."))
    |> testList "read token conformance"
