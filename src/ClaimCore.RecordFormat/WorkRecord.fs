namespace ClaimCore.RecordFormat

open System
open ClaimCore.Domain
open ClaimCore.Json

/// Resolved parent identity is retained independently of the caller's lookup spelling.
type WorkRequestRecord =
    {
        OperationId: OperationId
        CaseId: CaseId
        ExpectedRevision: Revision
        Update: ValidCaseWorkUpdate
    }

module internal WorkRecordCodecs =
    open Codec

    let invalid path =
        Error
            {
                Path = path
                Reason = "Invalid retained work value."
            }

    let identifier create value =
        uuid
        |> interpret
            (fun path raw -> create raw |> Option.map Ok |> Option.defaultValue (invalid path))
            value

    let operationId = identifier OperationId.create OperationId.value
    let caseId = identifier CaseId.create CaseId.value
    let actorId = identifier ActorId.create ActorId.value
    let reminderId = identifier ReminderId.create ReminderId.value

    let revision minimum =
        integer minimum SafeInteger.Maximum
        |> map (Revision.create >> Option.get) Revision.value

    let scalar parse format =
        text
        |> interpret
            (fun path value ->
                parse value
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid retained scalar."
                    }))
            format

    let summary = scalar HandlingSummary.parse HandlingSummary.format
    let action = scalar ReminderAction.parse ReminderAction.format
    let date = scalar CalendarDate.parse CalendarDate.format

    let work =
        (record (fun responsible summary ->
            {
                CaseWork.Responsible = responsible
                Summary = summary
            })
        : Fields<CaseWork, _>)
        |> field "responsibleActor" _.Responsible (nullable actorId)
        |> field "handlingSummary" _.Summary (nullable summary)
        |> build

    let update =
        CanonicalInputs.work
        |> interpret
            (fun path input ->
                CaseWorkInput.parse input
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid work input."
                    }))
            CaseWork.authored

    let decode codec bytes =
        Codec.decode LedgerPayload.MaximumBytes codec bytes
        |> Result.mapError (fun problem ->
            {
                Field = problem.Path
                Message = problem.Reason
            })

module WorkRecord =
    open Codec
    open WorkRecordCodecs

    let private requestCodec =
        (record (fun operation parent expected update () ->
            {
                WorkRequestRecord.OperationId = operation
                CaseId = parent
                ExpectedRevision = expected
                Update = update
            })
        : Fields<WorkRequestRecord, _>)
        |> field "operationId" _.OperationId operationId
        |> field "caseId" _.CaseId caseId
        |> field "expectedRevision" _.ExpectedRevision (revision 0L)
        |> field "values" _.Update update
        |> field "kind" (fun _ -> ()) (constant "UPDATE_WORK")
        |> build

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
        |> field "work" _.Work work
        |> build

    let encodeRequest request =
        requestCodec.Write request |> CanonicalWriter.bytes |> RecordLimits.requestBytes

    let decodeRequest (bytes: byte array) =
        if bytes.Length > RecordLimits.MaximumRequestRecordBytes then
            Error
                {
                    Field = "$"
                    Message = "Retained request exceeds its byte bound."
                }
        else
            decode requestCodec bytes

    let encodeSnapshot snapshot =
        if snapshot.Revision = Revision.zero && snapshot.Work <> CaseWork.empty then
            invalidArg (nameof snapshot) "Work revision zero must be empty."

        snapshotCodec.Write snapshot |> CanonicalWriter.bytes

    let decodeSnapshot bytes =
        decode snapshotCodec bytes
        |> Result.bind (fun value ->
            if value.Revision = Revision.zero && value.Work <> CaseWork.empty then
                Error
                    {
                        Field = "$.work"
                        Message = "Work revision zero must be empty."
                    }
            else
                Ok value)
