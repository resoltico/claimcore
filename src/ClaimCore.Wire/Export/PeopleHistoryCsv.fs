namespace ClaimCore.Wire.Export

open ClaimCore.Domain
open ClaimCore.Domain.Changes
open ClaimCore.Application.Contracts

/// A part may change several facts. Each CSV row retains the real accepting position.
module PeopleHistoryCsv =
    let private difference field format (value: ValueChange<'value>) =
        field,
        (value.Before |> Option.map format |> Option.defaultValue ""),
        (value.After |> Option.map format |> Option.defaultValue "")

    let values render (entry: PeopleHistoryEntry) =
        let metadata =
            [
                OperationId.format entry.OperationId
                Sequence.format (EffectPosition.sequence entry.Position)
                string (EffectPosition.index entry.Position)
                entry.OperationHash |> EntryHash.toBytes |> System.Convert.ToHexStringLower
                Csv.instant entry.RecordedAt
                ActorId.format entry.RecordedBy.ActorId
                Csv.actorName render entry.RecordedBy
                ActorId.format entry.Target.ActorId
                Csv.actorName render entry.Target
                TaskId.token entry.Task
                if entry.PrivateAvailable then "true" else "false"
            ]

        let changes = entry.Changes

        let differences =
            [
                for principal in changes.Principal |> Option.toList do
                    yield
                        difference
                            "principal.kind"
                            (fun principal ->
                                let kind, _, _ = PrincipalKey.parts principal in
                                Vocabulary.token PrincipalKind.vocabulary kind)
                            principal

                    yield
                        difference
                            "principal.issuer"
                            (fun principal ->
                                let _, issuer, _ = PrincipalKey.parts principal in issuer)
                            principal

                    yield
                        difference
                            "principal.subject"
                            (fun principal ->
                                let _, _, subject = PrincipalKey.parts principal in subject)
                            principal
                yield!
                    changes.Enabled
                    |> Option.map (
                        difference "enabled" (fun value -> if value then "true" else "false")
                    )
                    |> Option.toList
                yield!
                    changes.ProfilePresent
                    |> Option.map (
                        difference "profilePresent" (fun value -> if value then "true" else "false")
                    )
                    |> Option.toList
                yield!
                    changes.DisplayName
                    |> Option.map (difference "displayName" PartyName.format)
                    |> Option.toList
                yield!
                    changes.Contact
                    |> Option.map (difference "contact" ContactText.format)
                    |> Option.toList
                for assignment in changes.Grants do
                    let scope =
                        match assignment.Grant.Scope with
                        | Scope.Installation -> "INSTALLATION"
                        | Scope.Case id -> CaseId.format id

                    yield
                        difference
                            ("role." + Role.token assignment.Grant.Role)
                            (fun active -> if active then scope else "")
                            {
                                Before = Some assignment.Before
                                After = Some assignment.After
                            }
            ]

        (if differences.IsEmpty then [ "", "", "" ] else differences)
        |> List.map (fun (field, before, after) -> metadata @ [ field; before; after ])

    let csvHeader =
        Csv.rows
            [
                [
                    "operationId"
                    "sequence"
                    "effectIndex"
                    "operationHash"
                    "recordedAt"
                    "recordedBy.actorId"
                    "recordedBy.displayName"
                    "target.actorId"
                    "target.displayName"
                    "task"
                    "privateAvailable"
                    "field"
                    "before"
                    "after"
                ]
            ]

    let csvChunks render entries =
        entries |> Seq.collect (values render) |> Seq.map Csv.row

    let csvRows render entries =
        csvChunks render entries |> Seq.toArray |> Array.concat
