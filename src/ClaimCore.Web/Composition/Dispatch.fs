namespace ClaimCore.Web.Composition

open System.Threading
open System.Threading.Tasks
open ClaimCore.Application
open ClaimCore.Contract

type internal PreparedCall =
    IActorClaimsCore -> CancellationToken -> (unit -> unit) -> Task<JsonValue>

/// Typed closures erase handler types after codec admission, without casts or a second request DU.
module internal Dispatch =
    let private bind (endpoint: Endpoint<'Input, 'Output>) invoke =
        endpoint.Id,
        fun value ->
            endpoint.Input.Read "$" value
            |> Result.map (fun input ->
                fun core cancellation completed ->
                    task {
                        let! reply = invoke core input cancellation
                        completed ()
                        return endpoint.Output.Write reply
                    })

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
                bind Api.context (fun core () ct -> core.Context ct)
                bind Api.caseGet (fun core input ct -> core.Get(input, ct))
                bind Api.caseList (fun core input ct -> core.List(input, ct))
                bind Api.caseHistory (fun core input ct -> core.History(input, ct))
                bind Api.operationPreview (fun core input ct -> core.Preview(input, ct))
                bind Api.operationExecute (fun core input ct -> core.Execute(input, ct))
                bind Api.operationObserve (fun core input ct -> core.Observe(input, ct))
                bind Api.authorityList (fun core input ct -> core.ListActors(input, ct))
                bind Api.authorityGrants (fun core input ct -> core.ListGrants(input, ct))
            ]

        validate (List.map fst values)
        Map.ofList values

    let check () = bindings |> ignore
    let prepare endpoint value = bindings[Api.id endpoint] value
