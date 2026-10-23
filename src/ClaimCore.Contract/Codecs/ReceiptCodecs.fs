namespace ClaimCore.Contract

open ClaimCore.Contract.Protocol

module ReceiptCodecs =
    open Codec
    open ResponseScalars

    let disclosure =
        tagged
            "SnapshotDisclosure"
            "disclosure"
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
        |> field "caseReference" _.Reference reference
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

    let scope =
        tagged
            "ReceiptScope"
            "kind"
            [
                variant
                    OperationScopes.Case
                    ReceiptScope.Case
                    (function
                    | ReceiptScope.Case value -> Some value
                    | _ -> None)
                    caseFields
                variant
                    OperationScopes.Authority
                    ReceiptScope.Authority
                    (function
                    | ReceiptScope.Authority value -> Some value
                    | _ -> None)
                    authorityFields
            ]

    let receipt =
        (record (fun id position at by scope ->
            {
                OperationId = id
                Position = position
                RecordedAt = at
                RecordedBy = by
                Scope = scope
            })
        : Fields<OperationReceipt, _>)
        |> field "operationId" _.OperationId Codec.uuid
        |> field "position" _.Position ResponseCodecs.position
        |> field "recordedAt" _.RecordedAt Codec.instant
        |> field "recordedBy" _.RecordedBy Codec.uuid
        |> field "scope" _.Scope scope
        |> build
        |> named "OperationReceipt"
