namespace ClaimCore.Cli

open System.Threading
open System.Threading.Tasks
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Wire
open ClaimCore.Presentation

/// Human output obtains a fresh core work view after acceptance. It never infers missing-plan
/// counts from the single bounded effect, changes the accepted result, or adds machine-output frames.
module internal WorkRefresh =
    let private target (reply: CliReply) =
        match reply.Frame.Result with
        | FrameResult.Reply value when value.Route.Id = Api.operationExecute.Id ->
            ServiceReply.value Api.operationExecute value
            |> Option.bind (fun reply ->
                match reply with
                | RepresentationReply.Human {
                                                Outcome = HumanExecuteResult.Accepted document | HumanExecuteResult.AlreadyAccepted document
                                            } ->
                    match document.Context with
                    | InteractionContext.Receipt value -> value.RefreshCase
                    | _ -> None
                | _ -> None)
        | _ -> None

    let run (exchange: Invocation -> CancellationToken -> Task<CliReply>) deliver reply token =
        task {
            match target reply with
            | None -> ()
            | Some parent ->
                let frame =
                    Frames.create
                        Api.caseGet
                        {
                            CaseGetRequest.Target = CaseSelector.Id parent
                        }
                    |> Result.defaultWith (fun _ ->
                        invalidOp "A retained case identity is a valid work-refresh target.")

                let frame = Frames.withRepresentation Representation.Human frame
                let! current = exchange frame token
                deliver frame current
        }
