namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module AuthorityHistoryCodecs =
    open Codec
    open ResponseScalars

    let private principalFields = record id |> field "principal" id PrincipalCodecs.key


    let facts =
        tagged
            "AuthorityHistoryFacts"
            [
                variant
                    (AuthorityKind.token AuthorityKind.ProvisionOwner)
                    AuthorityHistoryFacts.OwnerProvisioned
                    (function
                    | AuthorityHistoryFacts.OwnerProvisioned value -> Some value
                    | _ -> None)
                    principalFields
                variant
                    (AuthorityKind.token AuthorityKind.Register)
                    AuthorityHistoryFacts.Registered
                    (function
                    | AuthorityHistoryFacts.Registered value -> Some value
                    | _ -> None)
                    principalFields
                variant
                    (AuthorityKind.token AuthorityKind.Grant)
                    AuthorityHistoryFacts.Granted
                    (function
                    | AuthorityHistoryFacts.Granted value -> Some value
                    | _ -> None)
                    ResponseCodecs.authorityGrantFields
                variant
                    (AuthorityKind.token AuthorityKind.Revoke)
                    AuthorityHistoryFacts.Revoked
                    (function
                    | AuthorityHistoryFacts.Revoked value -> Some value
                    | _ -> None)
                    ResponseCodecs.authorityGrantFields
                unitVariant (AuthorityKind.token AuthorityKind.Enable) AuthorityHistoryFacts.Enabled
                unitVariant
                    (AuthorityKind.token AuthorityKind.Disable)
                    AuthorityHistoryFacts.Disabled
            ]
        |> vocabulary (Vocabulary.name AuthorityKind.vocabulary)

    let entry =
        (record (fun sequence instant executor target facts ->
            {
                AuthorityHistoryEntry.Sequence = sequence
                RecordedAt = instant
                RecordedBy = executor
                TargetActorId = target
                Facts = facts
            })
        : Fields<AuthorityHistoryEntry, _>)
        |> field "sequence" _.Sequence sequence
        |> field "recordedAt" _.RecordedAt instant
        |> field "recordedBy" _.RecordedBy actorId
        |> field "targetActorId" _.TargetActorId actorId
        |> field "act" _.Facts facts
        |> build
        |> named "AuthorityHistoryEntry"

    let page =
        (record (fun entries next ->
            {
                AuthorityHistoryPage.Entries = entries
                NextCursor = next
            })
        : Fields<AuthorityHistoryPage, _>)
        |> field "entries" _.Entries (list Paging.MaximumPageSize entry)
        |> field "nextCursor" _.NextCursor (nullable (boundedText 1 512))
        |> build
        |> named "AuthorityHistoryPage"
