namespace ClaimCore.Database

open System
open System.Text.Json

[<NoEquality; NoComparison>]
type internal RestoreOwnerApprover =
    {
        ActorId: Guid
        GrantRevision: int64
        ApprovalEventId: Guid
    }

module internal DatabaseRestoreOwnerClaims =
    let private uuid name (item: JsonElement) =
        let raw =
            DatabaseRestoreCanonical.text name item
            |> Option.ofObj
            |> Option.defaultValue ""

        match Guid.TryParseExact(raw, "D") with
        | true, value when value <> Guid.Empty && value.ToString("D") = raw -> value
        | _ -> invalidOp "Restore report owner identity is invalid."

    let parse (root: JsonElement) (authorityRevision: int64) =
        let items = root.GetProperty("authorizedApprovers")

        if
            items.ValueKind <> JsonValueKind.Array
            || items.GetArrayLength() < 2
            || items.GetArrayLength() > 1000
        then
            invalidOp "Restore report owner roster is incomplete."

        let actors =
            [
                for item in items.EnumerateArray() do
                    if
                        not (
                            DatabaseRestoreCanonical.exactProperties
                                [ "actorId"; "approvalEventId"; "active"; "role"; "grantRevision" ]
                                item
                        )
                        || not (DatabaseRestoreCanonical.flag "active" item)
                        || DatabaseRestoreCanonical.text "role" item <> "owner"
                        || DatabaseRestoreCanonical.number "grantRevision" item < 1L
                        || DatabaseRestoreCanonical.number "grantRevision" item > authorityRevision
                    then
                        invalidOp "Restore report owner roster entry is invalid."

                    {
                        ActorId = uuid "actorId" item
                        GrantRevision = DatabaseRestoreCanonical.number "grantRevision" item
                        ApprovalEventId = uuid "approvalEventId" item
                    }
            ]

        if
            actors.Length <> (actors |> List.map _.ActorId |> Set.ofList |> Set.count)
            || actors.Length
               <> (actors |> List.map _.ApprovalEventId |> Set.ofList |> Set.count)
        then
            invalidOp "Restore report owner roster duplicates an actor."

        actors
