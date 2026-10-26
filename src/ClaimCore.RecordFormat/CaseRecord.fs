namespace ClaimCore.RecordFormat

open ClaimCore.Contract
open ClaimCore.Json
open ClaimCore.Contract.Protocol
open ClaimCore.Domain

module CaseRecord =
    let private live (snapshot: ClaimCore.Contract.CaseSnapshot) =
        match snapshot.View with
        | CaseView.Live(fields, disposition) -> fields, disposition
        | CaseView.Erased -> invalidArg (nameof snapshot) "A payload requires live case fields."

    let private codec: Codec<ClaimCore.Contract.CaseSnapshot> =
        Codec.record (fun revision disposition (fields: CaseFields) ->
            {
                Reference = fields.CaseReference
                Revision = revision
                View = CaseView.Live(fields, disposition)
            })
        |> Codec.field
            "revision"
            (fun (snapshot: CaseSnapshot) -> snapshot.Revision)
            RequestCodecs.sequence
        |> Codec.field
            "disposition"
            (live >> snd)
            (Codec.enumeration (Vocabulary.entries Dispositions.vocabulary))
        |> Codec.field "fields" (live >> fst) ResponseCodecs.caseFields
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
        | Ok { View = CaseView.Erased } -> Array.empty
        | Ok value -> codec.Write value |> CanonicalWriter.bytes

    let decodeSnapshot bytes =
        Codec.decode FormatLimits.MaximumPayloadBytes codec bytes
        |> Result.mapError (fun problem ->
            {
                Field = problem.Path
                Message = problem.Reason
            })
        |> Result.bind validate
