namespace ClaimCore.RecordFormat

open ClaimCore.Json
open ClaimCore.Domain

module CaseRecord =
    let private live (snapshot: CaseSnapshot) =
        LiveCase.ofState snapshot.State
        |> Option.defaultWith (fun () ->
            invalidArg (nameof snapshot) "A payload requires live case fields.")

    let private codec: Codec<CaseSnapshot> =
        Codec.record (fun caseId revision state ->
            let view = LiveCase.toState state

            let fields =
                match state with
                | LiveCase.Active fields
                | LiveCase.Voided fields -> fields

            {
                CaseId = caseId
                Reference = fields.CaseReference
                Revision = revision
                State = view
            })
        |> Codec.field
            "caseId"
            (fun (snapshot: CaseSnapshot) -> snapshot.CaseId)
            (Codec.uuid |> Codec.map (CaseId.create >> Option.get) CaseId.value)
        |> Codec.field
            "revision"
            (fun (snapshot: CaseSnapshot) -> snapshot.Revision)
            (Codec.integer 1L JsonValue.MaximumInteger
             |> Codec.map (Revision.create >> Option.get) Revision.value)
        |> Codec.field "state" live RecordCodecs.liveState
        |> Codec.build

        |> Codec.named "CaseSnapshotRecord"

    let private validate snapshot =
        match Case.ofAccepted snapshot with
        | Ok _ -> Ok snapshot
        | Error _ ->
            Error
                {
                    Field = "$.fields"
                    Message = "Invalid accepted case format."
                }

    let encodeSnapshot snapshot =
        match validate snapshot with
        | Error _ -> invalidArg (nameof snapshot) "Invalid accepted case format."
        | Ok { State = CaseState.Erased } -> Array.empty
        | Ok value -> codec.Write value |> CanonicalWriter.bytes

    let decodeSnapshot bytes =
        Codec.decode LedgerPayload.MaximumBytes codec bytes
        |> Result.mapError (fun problem ->
            {
                Field = problem.Path
                Message = problem.Reason
            })
        |> Result.bind validate

    let private decisionCodec =
        (Codec.record (fun after payment ->
            {
                CaseDecision.After = after
                Payment = payment
            })
        : Fields<CaseDecision, _>)
        |> Codec.field "after" _.After codec
        |> Codec.field "payment" _.Payment (Codec.nullable FinancialCodecs.difference)
        |> Codec.build
        |> Codec.named "CaseDecisionRecord"

    let encodeDecision (decision: CaseDecision) =
        match decision.After.State, decision.Payment with
        | CaseState.Erased, None -> Array.empty
        | CaseState.Erased, Some _ ->
            invalidArg (nameof decision) "Erasure cannot retain a payment effect."
        | _ ->
            validate decision.After
            |> Result.defaultWith (fun _ ->
                invalidArg (nameof decision) "Invalid accepted case decision.")
            |> ignore

            decisionCodec.Write decision |> CanonicalWriter.bytes

    let decodeDecision bytes =
        Codec.decode LedgerPayload.MaximumBytes decisionCodec bytes
        |> Result.mapError (fun problem ->
            {
                Field = problem.Path
                Message = problem.Reason
            })
        |> Result.bind (fun decision -> validate decision.After |> Result.map (fun _ -> decision))
