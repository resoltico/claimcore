namespace ClaimCore.Contract

open ClaimCore.Json

open ClaimCore.Contract.Protocol

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
        |> field (FieldTable.key BusinessField.CaseReference) _.Reference reference
        |> field "revision" _.Revision sequence
        |> field
            "act"
            _.Act
            (enumeration (CaseActKinds.all |> List.map (fun act -> act, CaseActKinds.token act)))
        |> field "snapshot" _.Snapshot disclosure

    let private authorityFields =
        (record (fun target act -> { TargetActorId = target; Act = act })
        : Fields<AuthorityReceipt, _>)
        |> field "targetActorId" _.TargetActorId Codec.uuid
        |> field
            "act"
            _.Act
            (enumeration (AuthorityKinds.all |> List.map (fun act -> act, AuthorityKinds.token act)))

    let private caseVariant =
        variant
            OperationSubjects.Case
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
                    OperationSubjects.Authority
                    ReceiptSubject.Authority
                    (function
                    | ReceiptSubject.Authority value -> Some value
                    | _ -> None)
                    authorityFields
            ]

    let private header scopeCodec =
        (record (fun id position at by subject ->
            {
                OperationId = id
                Position = position
                RecordedAt = at
                RecordedBy = by
                Subject = subject
            })
        : Fields<OperationReceipt, _>)
        |> field "operationId" _.OperationId Codec.uuid
        |> field "position" _.Position ResponseCodecs.position
        |> field "recordedAt" _.RecordedAt Codec.instant
        |> field "recordedBy" _.RecordedBy Codec.uuid
        |> field "subject" _.Subject scopeCodec
        |> build

    let receipt = header subject |> named "OperationReceipt"

    let caseReceipt =
        tagged "CaseReceiptSubject" [ caseVariant ] |> header |> named "CaseReceipt"
