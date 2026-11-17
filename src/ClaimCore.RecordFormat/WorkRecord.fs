namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

module WorkRecord =
    open Codec
    open RecordScalars

    let private snapshotCodec =
        (record (fun parent revision work ->
            {
                CaseWorkSnapshot.CaseId = parent
                Revision = revision
                Work = work
            })
        : Fields<CaseWorkSnapshot, _>)
        |> field "caseId" _.CaseId caseId
        |> field "revision" _.Revision (revision 0L)
        |> field "work" _.Work PartCodecs.work
        |> build

    let encodeSnapshot snapshot =
        if snapshot.Revision = Revision.zero && snapshot.Work <> CaseWork.empty then
            invalidArg (nameof snapshot) "Work revision zero must be empty."

        snapshotCodec.Write snapshot |> CanonicalWriter.bytes

    let decodeSnapshot bytes =
        Codec.decode Compartment.MaximumContentBytes snapshotCodec bytes
        |> Result.mapError (fun problem ->
            {
                DecodeProblem.Field = problem.Path
                Message = problem.Reason
            })
        |> Result.bind (fun value ->
            if value.Revision = Revision.zero && value.Work <> CaseWork.empty then
                Error
                    {
                        Field = "$.work"
                        Message = "Work revision zero must be empty."
                    }
            else
                Ok value)
