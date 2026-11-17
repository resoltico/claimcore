namespace ClaimCore.Application.Reads

open ClaimCore.Domain

/// Interprets the core assignee declaration against one actor and one case.
module internal AssigneeSelectionSql =
    [<RequireQualifiedAccess>]
    type Source =
        | ResponsibleCase
        | AssignedPair
        | SelectedCase

    let private tokens selection =
        match selection with
        | AssigneeSelection.EnabledHumanWithAll capabilities ->
            [
                yield "principal", Vocabulary.token PrincipalKind.vocabulary PrincipalKind.Human
                for capability in capabilities do
                    for role in Access.rolesFor capability do
                        yield Capability.token capability + ":" + Role.token role, Role.token role
            ]

    let parameters selection =
        tokens selection |> List.map snd |> List.toArray |> box

    let predicate selection parameter source =
        let actor = "a"

        let caseId =
            match source with
            | Source.ResponsibleCase -> "c.case_id"
            | Source.AssignedPair -> "p.case_id"
            | Source.SelectedCase -> "$1"

        let values = tokens selection

        let token key =
            let index = values |> List.findIndex (fun (candidate, _) -> candidate = key)
            $"({parameter}::text[])[{index + 1}]"

        match selection with
        | AssigneeSelection.EnabledHumanWithAll capabilities ->
            let rights =
                capabilities
                |> List.map (fun capability ->
                    if
                        not (Access.capabilityScopes capability |> List.contains ScopeKind.Case)
                    then
                        "FALSE"
                    else
                        let roles =
                            Access.rolesFor capability
                            |> List.map (fun role ->
                                token (Capability.token capability + ":" + Role.token role))

                        if roles.IsEmpty then
                            "FALSE"
                        else
                            let roleValues = String.concat "," roles
                            $"EXISTS(SELECT 1 FROM claimcore.grants g WHERE g.actor_id={actor}.actor_id AND (g.case_id IS NULL OR g.case_id={caseId}) AND g.role=ANY(ARRAY[{roleValues}]::text[]))")

            let human = token "principal"

            String.concat
                " AND "
                ([ $"{actor}.enabled"; $"{actor}.principal_kind={human}" ] @ rights)
