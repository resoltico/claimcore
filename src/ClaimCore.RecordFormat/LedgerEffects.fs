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

    let create operation count index =
        let supported =
            match operation with
            | None -> count = 1
            | Some AuthorityKind.Register
            | Some AuthorityKind.ProvisionOwner -> true
            | Some AuthorityKind.Grant
            | Some AuthorityKind.Revoke
            | Some AuthorityKind.Enable
            | Some AuthorityKind.Disable -> count = 1

        if supported && count >= 1 && count <= MaximumCount && index >= 0 && index < count then
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
        value.AuthorityOperation
        |> Option.exists (fun operation -> value.Count > 1 || operation = act)
