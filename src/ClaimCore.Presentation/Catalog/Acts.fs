namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Message identities follow concepts even when transport vocabularies are separate.
module PresentationKeys =
    let code vocabulary token = vocabulary + "." + token + ".label"

    let consequence kind =
        let part =
            match kind with
            | ActKind.Case(CaseActKind.Void | CaseActKind.Reinstate | CaseActKind.Erase)
            | ActKind.People(PeopleActKind.Admit | PeopleActKind.EraseProfile | PeopleActKind.Grant | PeopleActKind.Revoke | PeopleActKind.Disable) ->
                "consequence"
            | _ -> "meaning"

        Vocabulary.messageKey ActKind.vocabulary kind part
