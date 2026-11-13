namespace ClaimCore.Wire

open ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module PeopleHistoryCodecs =
    open Codec
    open ResponseScalars

    let private principalFields = record id |> field "principal" id PrincipalCodecs.key


    let facts =
        tagged
            "PeopleHistoryFacts"
            [
                variant
                    (PeopleActKind.token PeopleActKind.ProvisionOwner)
                    PeopleHistoryFacts.OwnerProvisioned
                    (function
                    | PeopleHistoryFacts.OwnerProvisioned value -> Some value
                    | _ -> None)
                    principalFields
                variant
                    (PeopleActKind.token PeopleActKind.Register)
                    PeopleHistoryFacts.Registered
                    (function
                    | PeopleHistoryFacts.Registered value -> Some value
                    | _ -> None)
                    principalFields
                variant
                    (PeopleActKind.token PeopleActKind.Grant)
                    PeopleHistoryFacts.Granted
                    (function
                    | PeopleHistoryFacts.Granted value -> Some value
                    | _ -> None)
                    ResponseCodecs.peopleGrantFields
                variant
                    (PeopleActKind.token PeopleActKind.Revoke)
                    PeopleHistoryFacts.Revoked
                    (function
                    | PeopleHistoryFacts.Revoked value -> Some value
                    | _ -> None)
                    ResponseCodecs.peopleGrantFields
                variant
                    (PeopleActKind.token PeopleActKind.SetProfile)
                    PeopleHistoryFacts.ProfileSet
                    (function
                    | PeopleHistoryFacts.ProfileSet profile -> Some profile
                    | _ -> None)
                    (record id |> field "profile" id (nullable ActorCodecs.profile))
                unitVariant (PeopleActKind.token PeopleActKind.EraseProfile) PeopleHistoryFacts.ProfileErased
                unitVariant (PeopleActKind.token PeopleActKind.Enable) PeopleHistoryFacts.Enabled
                unitVariant
                    (PeopleActKind.token PeopleActKind.Disable)
                    PeopleHistoryFacts.Disabled
            ]
        |> vocabulary (Vocabulary.name PeopleActKind.vocabulary)

    let entry =
        (record (fun operation position hash instant executor target facts ->
            {
                PeopleHistoryEntry.OperationId = operation
                Position = position
                OperationHash = hash
                RecordedAt = instant
                RecordedBy = executor
                Target = target
                Facts = facts
            })
        : Fields<PeopleHistoryEntry, _>)
        |> field "operationId" _.OperationId operationId
        |> field "position" _.Position LedgerCodecs.effectPosition
        |> field "operationHash" _.OperationHash LedgerCodecs.hash
        |> field "recordedAt" _.RecordedAt instant
        |> field "recordedBy" _.RecordedBy ActorCodecs.actorRef
        |> field "target" _.Target ActorCodecs.actorRef
        |> field "act" _.Facts facts
        |> build
        |> named "PeopleHistoryEntry"

    let page =
        (record (fun basis entries next ->
            {
                PeopleHistoryPage.Basis = basis
                Entries = entries
                NextCursor = next
            })
        : Fields<PeopleHistoryPage, _>)
        |> field "basis" _.Basis ResponseScalars.workDateBasis
        |> field "entries" _.Entries (list Paging.MaximumPageSize entry)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "PeopleHistoryPage"
