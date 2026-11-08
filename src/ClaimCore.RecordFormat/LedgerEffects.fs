namespace ClaimCore.RecordFormat

open ClaimCore.Domain

/// A bounded operation group has one root and contiguous effects with independently salted payload commitments.
[<StructuralEquality; StructuralComparison>]
type LedgerEffects =
    private
        {
            Index: int
            Count: int
            AuthorityOperation: AuthorityKind option
        }

module LedgerEffects =
    [<Literal>]
    let MaximumCount = 50

    let index value = value.Index
    let count value = value.Count
    let authorityOperation value = value.AuthorityOperation

    let private supportedCount operation count =
        match operation with
        | None -> count = 1
        | Some AuthorityKind.Admit -> count >= 3 && count <= MaximumCount
        | Some AuthorityKind.ProvisionOwner ->
            count >= 2 && count <= 2 + Authority.initialOwnerRoles.Length
        | Some AuthorityKind.Register -> count = 2
        | Some AuthorityKind.Grant
        | Some AuthorityKind.Revoke
        | Some AuthorityKind.Enable
        | Some AuthorityKind.Disable
        | Some AuthorityKind.SetProfile
        | Some AuthorityKind.EraseProfile -> count = 1

    let create operation count index =
        if
            supportedCount operation count
            && count >= 1
            && count <= MaximumCount
            && index >= 0
            && index < count
        then
            Some
                {
                    Index = index
                    Count = count
                    AuthorityOperation = operation
                }
        else
            None

    let single =
        {
            Index = 0
            Count = 1
            AuthorityOperation = None
        }

    let authority operation count index =
        create (Some operation) count index
        |> Option.defaultWith (fun () ->
            invalidArg (nameof count) "Invalid authority effect group.")

    let describesAuthority act value =
        match value.AuthorityOperation with
        | Some AuthorityKind.Admit ->
            value.Count >= 3
            && value.Count <= MaximumCount
            && act =
                (if value.Index = 0 then AuthorityKind.Register
                 elif value.Index = 1 then AuthorityKind.SetProfile
                 else AuthorityKind.Grant)
        | Some AuthorityKind.Register ->
            value.Count = 2
            && act =
                (if value.Index = 0 then
                     AuthorityKind.Register
                 else
                     AuthorityKind.SetProfile)
        | Some AuthorityKind.ProvisionOwner ->
            value.Count >= 2
            && value.Count <= 2 + Authority.initialOwnerRoles.Length
            && act =
                (if value.Index = 0 then AuthorityKind.ProvisionOwner
                 elif value.Index = 1 then AuthorityKind.SetProfile
                 else AuthorityKind.Grant)
        | Some operation -> value.Count = 1 && value.Index = 0 && operation = act
        | None -> false
