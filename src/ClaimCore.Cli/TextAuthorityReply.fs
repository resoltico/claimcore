namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json

module internal TextAuthorityReply =
    open TextReplyFormat

    let grantRow language (grant: AuthoredGrant) =
        let scope, reference =
            match grant.Scope with
            | GrantTarget.Installation ->
                term language ScopeKind.vocabulary ScopeKind.Installation, "—"
            | GrantTarget.CaseReference reference ->
                term language ScopeKind.vocabulary ScopeKind.Case, reference

        String.concat "  " [ term language Role.vocabulary grant.Role; scope; reference ]

    let actorRow language (actor: Actor) =
        let kind, issuer, subject = PrincipalKey.parts actor.Principal

        String.concat
            "  "
            [
                ActorId.format actor.ActorId
                principalKind language kind
                issuer
                subject
                boolean actor.Enabled
            ]

    let actorPage language (page: ActorReviewPage) =
        (page.Items |> List.map (actorRow language))
        @ nextLines language page.NextCursor

    let grantPage language (page: AuthorityGrantPage) =
        (page.Items |> List.map (grantRow language))
        @ nextLines language page.NextCursor

    let authorityChanges language (before: AuthorityState) (after: AuthorityState) =
        [
            (CliMessages.key CliMessage.EnabledLabel), boolean before.Enabled, boolean after.Enabled
            (CliMessages.key CliMessage.GrantsLabel),
            (before.Grants |> List.map (grantRow language) |> String.concat "; "),
            (after.Grants |> List.map (grantRow language) |> String.concat "; ")
        ]
        |> List.choose (fun (key, previous, next) ->
            if previous = next then
                None
            else
                Some(
                    line
                        language
                        key
                        ((if previous = "" then "—" else previous)
                         + " → "
                         + (if next = "" then "—" else next))
                ))
