namespace ClaimCore.Cli

open System
open System.Threading
open System.Threading.Tasks
open ClaimCore.Contract

/// Opaque retry keeps the selected technical metadata privately; it never reconstructs a request body.
module internal RecoverySelection =
    let private invocation endpoint input =
        JsonValue.Object [ "endpoint", JsonValue.String endpoint; "input", input ]
        |> WireWriter.bytes
        |> Frames.admit
        |> Result.defaultWith (fun _ -> invalidOp "Recovery request failed local admission.")

    let private failure (frame: Invocation) invalid =
        (if invalid then
             CliReply.protocol CliFailure.InvalidResponse
         else
             CliReply.local ClientFailure.Unavailable)
        |> CliReply.forAttempt frame DeliveryState.NotSent

    let private page (reply: CliReply) =
        match Frames.decode reply.Bytes with
        | Ok {
                 Delivery = DeliveryState.Delivered
                 Result = FrameResult.Reply body
             } ->
            match Api.operationPending.Output.Read "$" body with
            | Ok(ReadReply.Succeeded page) -> Ok page
            | Ok _ -> Error false
            | Error _ -> Error true
        | _ -> Error false

    let rec private find
        (send: Invocation -> (unit -> unit) -> CancellationToken -> Task<CliReply>)
        (request: RecoveryRequest)
        cursor
        seen
        token
        =
        task {
            let pending =
                invocation
                    Api.operationPending.Id
                    (Api.operationPending.Input.Write
                        {
                            AfterCursor = cursor
                            Limit = Limits.MaximumPageSize
                        })

            let! reply = send pending ignore token

            match page reply with
            | Error invalid -> return Error invalid
            | Ok page ->
                match
                    page.Items |> List.tryFind (fun item -> item.RecoveryId = request.RecoveryId)
                with
                | Some item when item.OperationId <> request.OperationId -> return Error true
                | Some item when List.contains RecoveryOffer.Retry item.Offers -> return Ok item
                | Some _ -> return Error false
                | None ->
                    match page.NextCursor with
                    | None -> return Error false
                    | Some next when Set.contains next seen -> return Error true
                    | Some next -> return! find send request (Some next) (Set.add next seen) token
        }

    let private matches (item: RecoveryItem) (receipt: OperationReceipt) =
        item.OperationId = receipt.OperationId
        && match item.Scope, receipt.Scope with
           | "CASE", ReceiptScope.Case value ->
               item.Act = CaseActKinds.token value.Act
               && (item.CaseReference
                   |> Option.exists (fun reference ->
                       String.Equals(
                           reference,
                           value.Reference,
                           StringComparison.OrdinalIgnoreCase
                       )))
               && (item.ExpectedRevision
                   |> Option.exists (fun revision ->
                       revision >= 0L
                       && revision < JsonValue.MaximumInteger
                       && value.Revision = revision + 1L))
           | "AUTHORITY", ReceiptScope.Authority value ->
               item.Act = AuthorityKinds.token value.Act
               && item.TargetActorId = Some value.TargetActorId
           | _ -> false

    let private correlated (frame: Invocation) item (reply: CliReply) =
        match Frames.decode reply.Bytes with
        | Ok { Result = FrameResult.Reply body } ->
            match Api.operationRetry.Output.Read "$" body with
            | Ok {
                     Outcome = ExecuteResult.Accepted receipt | ExecuteResult.AlreadyAccepted receipt
                 } when not (matches item receipt) -> failure frame true
            | Error _ -> failure frame true
            | _ -> reply
        | _ -> reply

    let invoke send frame beforeSend token =
        task {
            match Api.operationRetry.Input.Read "$" (Frames.input frame) with
            | Error _ -> return failure frame true
            | Ok request ->
                let! selected = find send request None Set.empty token

                match selected with
                | Error invalid -> return failure frame invalid
                | Ok item ->
                    let! reply = send frame beforeSend token
                    return correlated frame item reply
        }
