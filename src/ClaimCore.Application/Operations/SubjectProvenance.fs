namespace ClaimCore.Application

open ClaimCore.Domain
open ClaimCore.RecordFormat

/// The closed journal shape supplies positions; subjects own what those positions mean for rows.
module internal SubjectProvenance =
    let people (operation: RetainedOperation) =
        let effects = StoredEffect.ofOperation operation

        let grants =
            effects
            |> List.choose (fun effect ->
                match effect.Descriptor with
                | EffectDescriptor.People(_, PeopleEffectDescriptor.Grant grant) ->
                    Some(grant, effect.Position)
                | EffectDescriptor.People(_, PeopleEffectDescriptor.ProvisionOwner _) ->
                    Some(
                        {
                            ActorGrant.Role = Role.Owner
                            Scope = Scope.Installation
                        },
                        effect.Position
                    )
                | _ -> None)

        let erasure =
            effects
            |> List.tryPick (fun effect ->
                match effect.Descriptor with
                | EffectDescriptor.People(_, PeopleEffectDescriptor.EraseProfile) ->
                    Some effect.Position
                | _ -> None)

        {
            PeopleSubjectProvenance.Granted = Map.ofList grants
            ProfileErasure = erasure
        }
