namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

module PartiesScreen =
    let document (value: PartyChoicePage) =
        let columns =
            [
                ScreenEntries.column
                    "name"
                    "field.partyName.label"
                    Prominence.Primary
                    false
                    (fun (party: PartyChoice) ->
                        ScreenEntries.optional PresentedValue.Party party.Name)
                ScreenEntries.column
                    "role"
                    "field.partyRole.label"
                    Prominence.Primary
                    false
                    (fun party -> ScreenEntries.code PartyRole.vocabulary party.Role)
            ]

        let screen =
            ScreenEntries.choiceScreen
                "empty.parties"
                (fun (party: PartyChoice) -> PartyId.format party.PartyId)
                (fun party ->
                    let role = "role", ScreenEntries.code PartyRole.vocabulary party.Role

                    match party.Name with
                    | Some name ->
                        {
                            MessageId = "label.partyChoice"
                            Arguments = Map.ofList [ "name", PresentedValue.Party name; role ]
                        }
                    | None ->
                        {
                            MessageId = "label.partyChoiceUnnamed"
                            Arguments = Map.ofList [ role ]
                        })
                columns
                (fun _ -> None, [])
                value.Items

        HumanDocument.listPage value.Basis screen value.NextCursor
