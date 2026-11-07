namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Wire
open ClaimCore.Presentation
open ClaimCore.Presentation
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

    let actorRow language (actor: ActorReviewItem) =
        let kind, issuer, subject = PrincipalKey.parts actor.Principal

        String.concat
            "  "
            [
                ActorId.format actor.Actor.ActorId
                principalKind language kind
                TerminalText.data issuer
                displayed language (Some subject)
                displayed language (Some(actorName language actor.Actor))
                term language ActorAvailability.vocabulary actor.Actor.Availability
                actor.Contact |> Option.map ContactText.format |> displayed language
            ]

    let private table language definitions items =
        let options = TextTable.environment ()
        let header = TextTable.header language options definitions

        (if header = "" then [] else [ header ])
        @ TextTable.rows language options definitions items

    let actorPage language (page: ActorReviewPage) =
        table language Descriptor.actorColumns page.Items
        @ TextObservation.footer language page.Items.Length page.NextCursor page.Basis false

    let grantPage language (page: AuthorityGrantPage) =
        TextDescriptorValue.sections language (Descriptor.actor page.Actor)
        @ table language Descriptor.grantColumns page.Items
        @ TextObservation.footer language page.Items.Length page.NextCursor page.Basis false

    let historyPage language (page: AuthorityHistoryPage) =
        table language Descriptor.authorityHistoryColumns page.Entries
        @ TextObservation.footer language page.Entries.Length page.NextCursor page.Basis false

    let requests language (page: AccessRequestPage) =
        (page.Items
         |> List.collect (Descriptor.accessRequest >> TextDescriptorValue.sections language))
        @ TextObservation.footer language page.Items.Length page.NextCursor page.Basis false

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
