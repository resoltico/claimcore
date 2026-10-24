namespace ClaimCore.Contract

open ClaimCore.Contract.Protocol

module RecoveryCodecs =
    open Codec

    let retry =
        (record (fun id op -> { RecoveryId = id; OperationId = op }): Fields<RecoveryRequest, _>)
        |> field "recoveryId" _.RecoveryId uuid
        |> field "operationId" _.OperationId uuid
        |> build
        |> named "RecoveryRequest"

    let forget =
        (record (fun id -> { RecoveryId = id }): Fields<ForgetRequest, _>)
        |> field "recoveryId" _.RecoveryId uuid
        |> build
        |> named "ForgetRequest"

    let private offer =
        enumeration [ RecoveryOffer.Retry, "RETRY"; RecoveryOffer.Forget, "FORGET" ]

    let private caseAct =
        CaseActKinds.all
        |> List.map (fun value -> value, CaseActKinds.token value)
        |> enumeration

    let private authorityAct =
        RecoveryAuthorityKinds.all
        |> List.map (fun value -> value, RecoveryAuthorityKinds.token value)
        |> enumeration

    let private caseItem =
        (record (fun id op act reference revision () created expires offers ->
            {
                RecoveryId = id
                OperationId = op
                Subject =
                    RecoverySubject.Case
                        {
                            CaseReference = reference
                            ExpectedRevision = revision
                            Act = act
                        }
                CreatedAt = created
                ExpiresAt = expires
                Offers = offers
            })
        : Fields<RecoveryItem, _>)
        |> field "recoveryId" _.RecoveryId uuid
        |> field "operationId" _.OperationId uuid
        |> field
            "act"
            (fun item ->
                match item.Subject with
                | RecoverySubject.Case target -> target.Act
                | _ -> invalidOp "Expected a case recovery subject.")
            caseAct
        |> field
            "caseReference"
            (fun item -> item.CaseReference |> Option.get)
            ResponseScalars.reference
        |> field
            "expectedRevision"
            (fun item -> item.ExpectedRevision |> Option.get)
            (integer 0L JsonValue.MaximumInteger)
        |> field "targetActorId" (fun _ -> ()) nullValue
        |> field "createdAt" _.CreatedAt instant
        |> field "expiresAt" _.ExpiresAt instant
        |> field "offers" _.Offers (list 2 offer)
        |> build
        |> withScope "CASE"
        |> named "CaseRecoveryItem"

    let private authorityItem =
        (record (fun id op act reference () target created expires offers ->
            {
                RecoveryId = id
                OperationId = op
                Subject =
                    RecoverySubject.Authority
                        {
                            TargetActorId = target
                            CaseReference = reference
                            Act = act
                        }
                CreatedAt = created
                ExpiresAt = expires
                Offers = offers
            })
        : Fields<RecoveryItem, _>)
        |> field "recoveryId" _.RecoveryId uuid
        |> field "operationId" _.OperationId uuid
        |> field
            "act"
            (fun item ->
                match item.Subject with
                | RecoverySubject.Authority target -> target.Act
                | _ -> invalidOp "Expected an authority recovery subject.")
            authorityAct
        |> field "caseReference" _.CaseReference (nullable ResponseScalars.reference)
        |> field "expectedRevision" (fun _ -> ()) nullValue
        |> field "targetActorId" (fun item -> item.TargetActorId |> Option.get) uuid
        |> field "createdAt" _.CreatedAt instant
        |> field "expiresAt" _.ExpiresAt instant
        |> field "offers" _.Offers (list 2 offer)
        |> build
        |> withScope "AUTHORITY"
        |> named "AuthorityRecoveryItem"

    let item =
        choice
            "RecoveryItem"
            [
                branch
                    id
                    (fun item ->
                        match item.Subject with
                        | RecoverySubject.Case _ -> Some item
                        | _ -> None)
                    caseItem
                branch
                    id
                    (fun item ->
                        match item.Subject with
                        | RecoverySubject.Authority _ -> Some item
                        | _ -> None)
                    authorityItem
            ]
        |> discriminated "scope"

    let page =
        (record (fun items cursor -> { Items = items; NextCursor = cursor })
        : Fields<RecoveryPage, _>)
        |> field "items" _.Items (list Limits.MaximumPageSize item)
        |> field "nextCursor" _.NextCursor (nullable text)
        |> build
        |> named "RecoveryPage"

/// Admitted raw execution bytes are copied on entry and exit; callers cannot mutate a frozen request.
[<Sealed>]
type ExecutionEnvelope private (input: OperationInput, raw: byte array) =
    member _.Input = input
    member _.Bytes = Array.copy raw

    static member Admit(input, raw: byte array) =
        let frozen = Array.copy raw

        match Codec.decode Limits.MaximumRequestBytes RequestCodecs.operationInput frozen with
        | Ok decoded when decoded = input -> Ok(ExecutionEnvelope(input, frozen))
        | Ok _ ->
            Error
                {
                    Path = "$"
                    Reason = "Execution bytes differ from the admitted request."
                }
        | Error problem -> Error problem

    static member Create(input) =
        let bytes = RequestCodecs.operationInput.Write input |> WireWriter.bytes

        match ExecutionEnvelope.Admit(input, bytes) with
        | Ok value -> value
        | Error _ -> invalidArg (nameof input) "Use an admitted operation input."
