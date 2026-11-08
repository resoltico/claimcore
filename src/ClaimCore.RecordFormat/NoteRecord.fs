namespace ClaimCore.RecordFormat

open ClaimCore.Domain
open ClaimCore.Json

type NoteRequestRecord =
    {
        OperationId: OperationId
        Command: NoteCommand
    }

module NoteRecord =
    open Codec
    open WorkRecordCodecs

    let private noteId =
        uuid
        |> mapResult
            (fun path raw ->
                NoteId.create raw
                |> Option.map Ok
                |> Option.defaultValue (
                    Error
                        {
                            Path = path
                            Reason = "Invalid note ID."
                        }
                ))
            NoteId.value

    let private noteText =
        text
        |> mapResult
            (fun path raw ->
                NoteText.parse raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid note text."
                    }))
            NoteText.format

    let private note =
        (record Note.ofAccepted: Fields<Note, _>)
        |> field "noteId" Note.id noteId
        |> field "caseId" Note.caseId caseId
        |> field "text" Note.text (nullable noteText)
        |> build

    let private act =
        CanonicalInputs.noteAct
        |> mapResult
            (fun path raw ->
                NoteInput.parse raw
                |> Result.mapError (fun _ ->
                    {
                        Path = path
                        Reason = "Invalid note input."
                    }))
            NoteInput.authored

    let private request =
        (record (fun operation parent input ->
            {
                NoteRequestRecord.OperationId = operation
                Command = NoteInput.resolve parent input
            })
        : Fields<NoteRequestRecord, _>)
        |> field "operationId" _.OperationId operationId
        |> field "caseId" (fun request -> NoteCommand.parent request.Command) caseId
        |> field "act" (fun request -> NoteInput.ofCommand request.Command) act
        |> build

    let private snapshot = record id |> field "note" id note |> build

    let encodeRequest value =
        request.Write value |> CanonicalWriter.bytes |> RecordLimits.requestBytes

    let decodeRequest bytes = decode request bytes

    let encodeSnapshot value =
        snapshot.Write value |> CanonicalWriter.bytes

    let decodeSnapshot bytes = decode snapshot bytes
