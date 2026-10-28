namespace ClaimCore.Protocol

open ClaimCore.Protocol

open ClaimCore.Domain

open ClaimCore.Json


module ReceiptCodecs =
    open Codec
    open ResponseScalars

    let disclosure =
        tagged
            "SnapshotDisclosure"
            [
                variant
                    "DISCLOSED"
                    SnapshotDisclosure.Disclosed
                    (function
                    | SnapshotDisclosure.Disclosed value -> Some value
                    | _ -> None)
                    (record id |> field "view" id ResponseCodecs.liveState)
                unitVariant "WITHHELD" SnapshotDisclosure.Withheld
                unitVariant "ERASED" SnapshotDisclosure.Erased
            ]

    let private caseFields =
        (record (fun reference revision act snapshot ->
            {
                Reference = reference
                Revision = revision
                Act = act
                Snapshot = snapshot
            })
        : Fields<CaseReceipt, _>)
        |> field (BusinessField.key BusinessField.CaseReference) _.Reference caseReference
        |> field "revision" _.Revision positiveRevision
        |> field "act" _.Act (VocabularyCodec.create CaseActKind.vocabulary)
        |> field "snapshot" _.Snapshot disclosure

    let private authorityFields =
        (record (fun target act -> { TargetActorId = target; Act = act })
        : Fields<AuthorityReceipt, _>)
        |> field "targetActorId" _.TargetActorId actorId
        |> field "act" _.Act (VocabularyCodec.create AuthorityKind.vocabulary)

    let private caseVariant =
        variant
            (ActSubject.token ActSubject.Case)
            ReceiptSubject.Case
            (function
            | ReceiptSubject.Case value -> Some value
            | _ -> None)
            caseFields

    let subject =
        tagged
            "ReceiptSubject"
            [
                caseVariant
                variant
                    (ActSubject.token ActSubject.Authority)
                    ReceiptSubject.Authority
                    (function
                    | ReceiptSubject.Authority value -> Some value
                    | _ -> None)
                    authorityFields
            ]

    let private header subjectCodec =
        (record (fun id position at by subject ->
            {
                OperationId = id
                Position = position
                RecordedAt = at
                RecordedBy = by
                Subject = subject
            })
        : Fields<OperationReceipt, _>)
        |> field "operationId" _.OperationId operationId
        |> field "position" _.Position ResponseCodecs.position
        |> field "recordedAt" _.RecordedAt Codec.instant
        |> field "recordedBy" _.RecordedBy actorId
        |> field "subject" _.Subject subjectCodec
        |> build

    let receipt = header subject |> named "OperationReceipt"

    let caseReceipt =
        tagged "CaseReceiptSubject" [ caseVariant ] |> header |> named "CaseReceipt"
