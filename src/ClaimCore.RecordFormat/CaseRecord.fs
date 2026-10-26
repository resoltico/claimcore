namespace ClaimCore.RecordFormat

open ClaimCore.Contract
open ClaimCore.Json
open ClaimCore.Domain

module CaseRecord =
    let private live (snapshot: CaseSnapshot) =
        LiveCase.ofState snapshot.State
        |> Option.defaultWith (fun () ->
            invalidArg (nameof snapshot) "A payload requires live case fields.")

    let private codec: Codec<CaseSnapshot> =
        Codec.record (fun revision state ->
            let view = LiveCase.toState state
            let fields = CaseState.fields view |> Option.get

            {
                Reference = fields.CaseReference
                Revision = revision
                State = view
            })
        |> Codec.field
            "revision"
            (fun (snapshot: CaseSnapshot) -> snapshot.Revision)
            (Codec.integer 1L JsonValue.MaximumInteger)
        |> Codec.field "state" live RecordCodecs.liveState
        |> Codec.build
        |> Codec.withFormat (int64 RecordVersions.Snapshot)
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
        Codec.decode Formats.MaximumPayloadBytes codec bytes
        |> Result.mapError (fun problem ->
            {
                Field = problem.Path
                Message = problem.Reason
            })
        |> Result.bind validate
