namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries
open PeopleScreen

module ContextScreen =
    let context (reply: ContextReply) =
        let state, details =
            match reply.Requester with
            | RequesterState.Active item ->
                RequesterKind.Active,
                [
                    value "self" "label.account" Prominence.Primary (PresentedValue.Actor item.Self)
                    value
                        "capabilities"
                        "label.capabilities"
                        Prominence.Primary
                        (codes Capability.vocabulary item.Capability)
                    value
                        "views"
                        "label.views"
                        Prominence.Secondary
                        (codes View.vocabulary item.View)
                    value
                        "issuer"
                        "field.principal.issuer.label"
                        Prominence.Detail
                        (PresentedValue.Identifier item.Issuer)
                    value
                        "installationId"
                        "label.installationId"
                        Prominence.Detail
                        (PresentedValue.Identifier(item.InstallationId.ToString("D")))
                    value
                        "productVersion"
                        "label.productVersion"
                        Prominence.Detail
                        (PresentedValue.Identifier item.Runtime.ProductVersion)
                ]
            | RequesterState.NoRoles item ->
                RequesterKind.NoRoles,
                [
                    value "self" "label.account" Prominence.Primary (PresentedValue.Actor item.Self)
                    value
                        "installationId"
                        "label.installationId"
                        Prominence.Detail
                        (PresentedValue.Identifier(item.InstallationId.ToString("D")))
                    value
                        "businessDate"
                        "label.businessDate"
                        Prominence.Secondary
                        (PresentedValue.Date(CalendarDate.ofDate item.BusinessDate))
                    value
                        "timeZone"
                        "label.timeZone"
                        Prominence.Secondary
                        (PresentedValue.Text item.TimeZone)
                ]
            | RequesterState.Disabled item ->
                RequesterKind.Disabled,
                [ value "self" "label.account" Prominence.Primary (PresentedValue.Actor item) ]
            | RequesterState.NotRegistered item ->
                RequesterKind.NotRegistered,
                claims item.Claims
                @ [
                    value
                        "issuer"
                        "field.principal.issuer.label"
                        Prominence.Detail
                        (PresentedValue.Identifier item.Issuer)
                    value
                        "subject"
                        "field.principal.subject.label"
                        Prominence.Detail
                        (PresentedValue.Identifier item.Subject)
                ]

        [
            section
                "label.account"
                ([
                    value
                        "state"
                        "label.accountState"
                        Prominence.Primary
                        (code RequesterKind.vocabulary state)
                    value
                        "guidance"
                        "label.account"
                        Prominence.Primary
                        (PresentedValue.Message(
                            Vocabulary.messageKey RequesterKind.vocabulary state "meaning"
                        ))
                 ]
                 @ details)
        ]
        @ (match reply.Requester with
           | RequesterState.Active item -> roleSections item.Roles
           | RequesterState.NoRoles _
           | RequesterState.Disabled _
           | RequesterState.NotRegistered _ -> [])

    let session authenticated contractIdentity =
        [
            section
                "label.account"
                [
                    value
                        "authenticated"
                        "label.authenticated"
                        Prominence.Primary
                        (boolean authenticated)
                ]
            section
                "label.technicalDetails"
                [
                    value
                        "contractIdentity"
                        "label.contractIdentity"
                        Prominence.Detail
                        (PresentedValue.Identifier contractIdentity)
                ]
        ]

    let logout () =
        [
            section
                "label.account"
                [
                    value
                        "result"
                        "label.accountState"
                        Prominence.Primary
                        (PresentedValue.Message "label.signedOut")
                ]
        ]


    let view (reply: ContextReply) =
        let state, identity, facts, details =
            match reply.Requester with
            | RequesterState.Active item ->
                RequesterKind.Active,
                [
                    value "self" "label.account" Prominence.Primary (PresentedValue.Actor item.Self)
                    value
                        "roles"
                        "field.role.label"
                        Prominence.Secondary
                        (codes Role.vocabulary (item.Roles |> List.map _.Role |> List.distinct))
                ],
                [
                    value
                        "businessDate"
                        "label.businessDate"
                        Prominence.Secondary
                        (PresentedValue.Date(CalendarDate.ofDate item.Runtime.Basis.BusinessDate))
                    value
                        "timeZone"
                        "label.timeZone"
                        Prominence.Secondary
                        (PresentedValue.Text item.Runtime.Basis.TimeZoneId)
                    value
                        "maintenance"
                        "label.maintenance"
                        Prominence.Secondary
                        (boolean item.Maintenance.Active)
                ],
                [
                    value
                        "issuer"
                        "field.principal.issuer.label"
                        Prominence.Detail
                        (PresentedValue.Identifier item.Issuer)
                    value
                        "installationId"
                        "label.installationId"
                        Prominence.Detail
                        (PresentedValue.Identifier(item.InstallationId.ToString("D")))
                    value
                        "productVersion"
                        "label.productVersion"
                        Prominence.Detail
                        (PresentedValue.Identifier item.Runtime.ProductVersion)
                ]
            | RequesterState.NoRoles item ->
                RequesterKind.NoRoles,
                [
                    value "self" "label.account" Prominence.Primary (PresentedValue.Actor item.Self)
                ],
                [
                    value
                        "businessDate"
                        "label.businessDate"
                        Prominence.Secondary
                        (PresentedValue.Date(CalendarDate.ofDate item.BusinessDate))
                    value
                        "timeZone"
                        "label.timeZone"
                        Prominence.Secondary
                        (PresentedValue.Text item.TimeZone)
                ],
                [
                    value
                        "installationId"
                        "label.installationId"
                        Prominence.Detail
                        (PresentedValue.Identifier(item.InstallationId.ToString("D")))
                ]
            | RequesterState.Disabled item ->
                RequesterKind.Disabled,
                [ value "self" "label.account" Prominence.Primary (PresentedValue.Actor item) ],
                [],
                []
            | RequesterState.NotRegistered item ->
                RequesterKind.NotRegistered,
                claims item.Claims,
                [],
                [
                    value
                        "issuer"
                        "field.principal.issuer.label"
                        Prominence.Detail
                        (PresentedValue.Identifier item.Issuer)
                    value
                        "subject"
                        "field.principal.subject.label"
                        Prominence.Detail
                        (PresentedValue.Identifier item.Subject)
                ]

        let identity =
            [
                value
                    "state"
                    "label.accountState"
                    Prominence.Primary
                    (code RequesterKind.vocabulary state)
                value
                    "guidance"
                    "label.account"
                    Prominence.Secondary
                    (PresentedValue.Message(
                        Vocabulary.messageKey RequesterKind.vocabulary state "meaning"
                    ))
            ]
            @ identity

        let heading =
            match state with
            | RequesterKind.Active -> None
            | RequesterKind.Disabled -> Some "label.accessDisabled"
            | _ -> Some "label.accessPending"

        Screen.create
            [
                entryRegion RegionRole.Identity heading identity
                match reply.Requester with
                | RequesterState.NotRegistered _ ->
                    region
                        RegionRole.Notice
                        None
                        (RegionContent.Notice
                            [
                                {
                                    MessageId = "label.accessRequested"
                                    Reason = None
                                    Tone = NoticeTone.Info
                                }
                            ])
                | RequesterState.Active item when item.Maintenance.Active ->
                    region
                        RegionRole.Notice
                        None
                        (RegionContent.Notice
                            [
                                {
                                    MessageId = "diagnostic.REGISTER_MAINTENANCE"
                                    Reason = None
                                    Tone = NoticeTone.Warning
                                }
                            ])
                | _ -> ()
                if not facts.IsEmpty then
                    entryRegion RegionRole.Facts None facts
                if not details.IsEmpty then
                    entryRegion RegionRole.Details (Some "label.technicalDetails") details
            ]

    let sessionView authenticated contractIdentity =
        let sections = session authenticated contractIdentity

        Screen.create
            [
                entryRegion RegionRole.Identity None sections[0].Entries
                entryRegion RegionRole.Details (Some "label.technicalDetails") sections[1].Entries
            ]

    let logoutView () =
        Screen.create [ entryRegion RegionRole.Notice None (logout () |> List.collect _.Entries) ]

    let document (value: ContextReply) =
        let identity, basis, actions, links =
            match value.Requester with
            | RequesterState.Active actor ->
                let actions =
                    if actor.Maintenance.Active then
                        []
                    else
                        actor.Offers
                        |> List.map (TaskAffordances.installationOffer (Some actor.Issuer))

                let links =
                    actor.View
                    |> List.map (function
                        | View.Cases ->
                            NamedLink.create
                                "view.CASES.label"
                                LinkRelation.View
                                (Link.caseList (Some CaseView.NeedsAttention))
                        | View.People ->
                            NamedLink.create
                                "view.PEOPLE.label"
                                LinkRelation.View
                                (Link.PeopleList
                                    {
                                        Role = None
                                        AfterCursor = None
                                        Limit = Paging.DefaultPageSize
                                        Text = None
                                    })
                        | View.Operations ->
                            NamedLink.create
                                "view.OPERATIONS.label"
                                LinkRelation.View
                                Link.OperationLookupForm)

                let links =
                    links
                    @ [
                        if List.contains View.Cases actor.View then
                            yield
                                NamedLink.create
                                    "label.reminders"
                                    LinkRelation.View
                                    (Link.reminderList ReminderView.Pending)
                        if List.contains Capability.ReadAccess actor.Capability then
                            yield
                                NamedLink.create
                                    "endpoint.people.history.label"
                                    LinkRelation.View
                                    (Link.PeopleHistory
                                        {
                                            TargetActorId = None
                                            AfterCursor = None
                                            Limit = Paging.DefaultPageSize
                                        })
                        if List.contains Capability.ReadDisclosureTrail actor.Capability then
                            yield
                                NamedLink.create
                                    "endpoint.disclosure.trail.label"
                                    LinkRelation.View
                                    (Link.DisclosureTrail
                                        {
                                            ActorId = None
                                            CaseId = None
                                            Kind = None
                                            From = None
                                            Until = None
                                            AfterCursor = None
                                            Limit = Paging.DefaultPageSize
                                        })
                        if List.contains Capability.ManageAccess actor.Capability then
                            yield
                                NamedLink.create
                                    "endpoint.people.requests.label"
                                    LinkRelation.View
                                    (Link.PeopleRequests
                                        {
                                            AfterCursor = None
                                            Limit = Paging.DefaultPageSize
                                        })
                    ]

                {
                    State = RequesterKind.Active
                    InstallationId = Some actor.InstallationId
                    ActorId = Some actor.ActorId
                },
                ObservationBasis.Snapshot actor.Runtime.Basis,
                actions,
                links
            | RequesterState.NoRoles actor ->
                {
                    State = RequesterKind.NoRoles
                    InstallationId = Some actor.InstallationId
                    ActorId = Some actor.Self.ActorId
                },
                ObservationBasis.Unavailable,
                [],
                []
            | RequesterState.Disabled actor ->
                {
                    State = RequesterKind.Disabled
                    InstallationId = None
                    ActorId = Some actor.ActorId
                },
                ObservationBasis.Unavailable,
                [],
                []
            | RequesterState.NotRegistered _ ->
                {
                    State = RequesterKind.NotRegistered
                    InstallationId = None
                    ActorId = None
                },
                ObservationBasis.Unavailable,
                [],
                []

        HumanDocument.create
            (DocumentSubject.Requester identity)
            (view value)
            (actions @ [ Affordance.command CommandRequest.TerminateOwnSessions ])
            links
            None
            basis
            HumanDisclosure.Current
            None
