namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.Json

module internal TextAuthorityReply =
    open TextReplyFormat

    let grantRow language (grant: ScopedGrant) =
        let scope, reference =
            match grant.Scope with
            | GrantScope.Installation ->
                term language ScopeKind.vocabulary ScopeKind.Installation, "—"
            | GrantScope.Case(_, reference) ->
                term language ScopeKind.vocabulary ScopeKind.Case, CaseReference.format reference

        String.concat
            "  "
            [ term language Role.vocabulary grant.Role; scope; TerminalText.data reference ]

    let actorRow language (actor: Actor) =
        let kind, issuer, subject = PrincipalKey.parts actor.Principal

        String.concat
            "  "
            [
                ActorId.format actor.ActorId
                principalKind language kind
                TerminalText.data issuer
                displayed language (Some subject)
                boolean actor.Enabled
            ]

    let actorPage language (page: ActorReviewPage) =
        (page.Items |> List.map (actorRow language))
        @ nextLines language page.NextCursor

    let grantPage language (page: AuthorityGrantPage) =
        (page.Items |> List.map (grantRow language))
        @ nextLines language page.NextCursor

    let historyPage language (page: AuthorityHistoryPage) =
        let empty =
            if page.Entries.IsEmpty then
                [ label language (CliMessages.key CliMessage.AuthorityHistoryEmpty) ]
            else
                []

        empty
        @ (page.Entries
           |> List.map (fun entry ->
               let facts =
                   match entry.Facts with
                   | AuthorityHistoryFacts.OwnerProvisioned principal
                   | AuthorityHistoryFacts.Registered principal ->
                       let kind, issuer, subject = PrincipalKey.parts principal

                       String.concat
                           "  "
                           [
                               principalKind language kind
                               TerminalText.data issuer
                               displayed language (Some subject)
                           ]
                   | AuthorityHistoryFacts.Granted grant
                   | AuthorityHistoryFacts.Revoked grant -> grantRow language grant
                   | AuthorityHistoryFacts.Enabled
                   | AuthorityHistoryFacts.Disabled -> ""

               String.concat
                   "  "
                   [
                       Sequence.format entry.Sequence
                       instant entry.RecordedAt
                       term
                           language
                           AuthorityKind.vocabulary
                           (AuthorityHistoryFacts.kind entry.Facts)
                       ActorId.format entry.RecordedBy
                       ActorId.format entry.TargetActorId
                       facts
                   ]))
        @ nextLines language page.NextCursor

    let private accessText language (access: CapabilityAccess) =
        let roles values =
            if List.isEmpty values then
                "—"
            else
                values |> List.map (term language Role.vocabulary) |> String.concat ", "

        MessageCatalogs.renderCli
            language
            (CliMessage.AccessSources(
                roles access.DirectRoles,
                roles access.InstallationRoles,
                boolean access.Effective
            ))

    let authorityStates language (before: AuthorityState) (after: AuthorityState) =
        [
            (CliMessages.key CliMessage.EnabledLabel), boolean before.Enabled, boolean after.Enabled
            (CliMessages.key CliMessage.GrantsLabel),
            (before.Grants |> List.map (grantRow language) |> String.concat "; "),
            (after.Grants |> List.map (grantRow language) |> String.concat "; ")
        ]
        |> List.map (fun (key, previous, next) ->
            line
                language
                key
                ((if previous = "" then "—" else previous)
                 + " → "
                 + (if next = "" then "—" else next)))

    let accessStates language (review: AuthorityReview) =
        let scope, reference =
            match review.AccessScope with
            | GrantScope.Installation -> ScopeKind.Installation, ""
            | GrantScope.Case(_, reference) ->
                ScopeKind.Case, " " + TerminalText.data (CaseReference.format reference)

        let sources (values: CapabilityAccess list) capability =
            values
            |> List.tryFind (fun value -> value.Capability = capability)
            |> Option.map (accessText language)
            |> Option.defaultValue "—"

        line language "input.scope.label" (term language ScopeKind.vocabulary scope + reference)
        :: (Capability.all
            |> List.filter (fun capability ->
                (review.Before.Access @ review.After.Access)
                |> List.exists (fun value -> value.Capability = capability))
            |> List.map (fun capability ->
                term language Capability.vocabulary capability
                + ": "
                + sources review.Before.Access capability
                + " → "
                + sources review.After.Access capability))
