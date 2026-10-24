namespace ClaimCore.Web.Composition

open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Contract
open ClaimCore.Contract.Protocol

[<NoEquality; NoComparison>]
type internal PreparedCall =
    {
        Invoke: IActorClaimsCore -> CancellationToken -> (unit -> unit) -> Task<JsonValue>
        Unavailable: unit -> JsonValue
    }

/// Typed closures erase handler types after codec admission, without casts or a second request DU.
module internal Dispatch =
    let private bind (endpoint: Endpoint<'Input, 'Output>) unavailable invoke =
        endpoint.Id,
        fun value ->
            endpoint.Input.Read "$" value
            |> Result.map (fun input ->
                {
                    Unavailable = fun () -> endpoint.Output.Write(unavailable input)
                    Invoke =
                        fun core cancellation completed ->
                            task {
                                let! reply = invoke core input cancellation
                                completed ()
                                return endpoint.Output.Write reply
                            }
                })

    let private bindRead endpoint invoke =
        bind endpoint (fun _ -> ReadReply.Failed CoreFault.StoreUnavailable) invoke

    let private previewUnavailable input : PreviewReply =
        {
            OperationId = OperationInput.operationId input
            Outcome = PreviewResult.Failed CoreFault.StoreUnavailable
        }

    let private executeUnavailable input : ExecuteReply =
        {
            RecoveryId = None
            OperationId = OperationInput.operationId input
            Outcome = ExecuteResult.Failed CoreFault.StoreUnavailable
        }

    let validate ids =
        let expected =
            Api.all
            |> List.map Api.id
            |> List.filter (fun id -> id <> Api.session.Id && id <> Api.logout.Id)
            |> Set.ofList

        if
            ids |> List.distinct |> List.length <> List.length ids
            || Set.ofList ids <> expected
        then
            invalidOp "Application dispatch bindings must match the endpoint registry exactly."

    let private bindings =
        let values: (string * (JsonValue -> Result<PreparedCall, Problem>)) list =
            [
                bindRead Api.context (fun core () ct -> core.Context ct)
                bindRead Api.caseGet (fun core input ct -> core.Get(input, ct))
                bindRead Api.caseList (fun core input ct -> core.List(input, ct))
                bindRead Api.caseHistory (fun core input ct -> core.History(input, ct))
                bind Api.operationPreview previewUnavailable (fun core input ct ->
                    core.Preview(input, ct))
                bind Api.operationExecute executeUnavailable (fun core input ct ->
                    core.Execute(input, ct))
                bindRead Api.operationPending (fun core input ct -> core.Pending(input, ct))
                bind
                    Api.operationRetry
                    (fun input ->
                        {
                            RecoveryId = Some input.RecoveryId
                            OperationId = input.OperationId
                            Outcome = ExecuteResult.Failed CoreFault.StoreUnavailable
                        })
                    (fun core input ct -> core.Retry(input, ct))
                bindRead Api.operationForget (fun core input ct -> core.Forget(input, ct))
                bindRead Api.operationObserve (fun core input ct -> core.Observe(input, ct))
                bindRead Api.authorityList (fun core input ct -> core.ListActors(input, ct))
                bindRead Api.authorityGrants (fun core input ct -> core.ListGrants(input, ct))
            ]

        validate (List.map fst values)
        Map.ofList values

    let check () = bindings |> ignore
    let prepare endpoint value = bindings[Api.id endpoint] value

    let prepareRaw endpoint value bytes =
        if Api.id endpoint = Api.operationExecute.Id then
            RequestCodecs.operationInput.Read "$" value
            |> Result.bind (fun input ->
                ExecutionEnvelope.Admit(input, bytes)
                |> Result.map (fun envelope ->
                    {
                        Unavailable =
                            fun () -> Api.operationExecute.Output.Write(executeUnavailable input)
                        Invoke =
                            fun core token completed ->
                                task {
                                    let! reply = core.ExecuteEnvelope(envelope, token)
                                    completed ()
                                    return Api.operationExecute.Output.Write reply
                                }
                    }))
        else
            prepare endpoint value
