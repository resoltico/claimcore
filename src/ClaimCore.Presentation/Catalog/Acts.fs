namespace ClaimCore.Presentation

open ClaimCore.Domain
open ClaimCore.Application.Contracts

/// Message identities follow concepts even when transport vocabularies are separate.
module PresentationKeys =
    let code vocabulary token =
        match vocabulary with
        | "noteAct" -> "act." + token + ".label"
        | "workAct" -> "act." + token + ".label"
        | "reminderAct" -> "act.REMINDER_" + token + ".label"
        | _ -> vocabulary + "." + token + ".label"

    let consequence kind =
        let part =
            match kind with
            | ActKind.Case(CaseActKind.Void | CaseActKind.Reinstate | CaseActKind.Erase)
            | ActKind.Authority(AuthorityKind.Admit | AuthorityKind.EraseProfile | AuthorityKind.Grant | AuthorityKind.Revoke | AuthorityKind.Disable) ->
                "consequence"
            | _ -> "meaning"

        Vocabulary.messageKey ActKind.vocabulary kind part
