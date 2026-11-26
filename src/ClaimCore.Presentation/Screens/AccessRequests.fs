namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module AccessRequestsScreen =
    let accessRequestColumns =
        [
            column "issuer" "field.principal.issuer.label" Prominence.Primary false (fun item ->
                let _, issuer, _ = PrincipalKey.parts item.Principal in
                PresentedValue.Identifier issuer)
            column "subject" "field.principal.subject.label" Prominence.Primary false (fun item ->
                let _, _, subject = PrincipalKey.parts item.Principal in
                PresentedValue.Identifier subject)
            column
                "principalKind"
                "field.principal.kind.label"
                Prominence.Secondary
                false
                (fun item ->
                    let kind, _, _ = PrincipalKey.parts item.Principal in
                    code PrincipalKind.vocabulary kind)
            column "nameHint" "label.requestedName" Prominence.Secondary false (fun item ->
                optional PresentedValue.Party item.Claims.Name)
            column "usernameHint" "label.requestedUsername" Prominence.Secondary false (fun item ->
                optional PresentedValue.Party item.Claims.PreferredUsername)
            column
                "profileHintStatus"
                "label.profileHintStatus"
                Prominence.Secondary
                false
                (fun item -> profileHintStatus item.Claims)
            column "emailHint" "label.requestedEmail" Prominence.Secondary false (fun item ->
                optional PresentedValue.Party item.Claims.Email)
            column "emailHintStatus" "label.emailHintStatus" Prominence.Secondary false (fun item ->
                emailHintStatus item.Claims)
            column "firstSeen" "label.firstSeen" Prominence.Secondary false (fun item ->
                PresentedValue.Instant item.FirstSeen)
            column "lastSeen" "label.lastSeen" Prominence.Secondary false (fun item ->
                PresentedValue.Instant item.LastSeen)
        ]

    let accessRequest item =
        [ section "label.accessRequest" (entries accessRequestColumns item) ]

    let dismissedAccessRequest () =
        [
            section
                "label.accessRequest"
                [
                    value
                        "result"
                        "label.action"
                        Prominence.Primary
                        (PresentedValue.Message "label.requestDismissed")
                ]
        ]


    let rows rows =
        resultScreenWithControls
            "label.noAccessRequests"
            (fun (row: AccessRequest) -> principalKey row.Principal)
            accessRequestColumns
            (fun row -> None, TaskAffordances.requestActions row)
            rows

    let view (reply: AccessRequestPage) =
        let screen = rows reply.Items
        let count = int64 reply.Items.Length
        Screen.withRegions (List.map (withCount "accessRequests" count) screen.Regions) screen

    let dismissed () =
        Screen.create
            [
                region
                    RegionRole.Notice
                    None
                    (RegionContent.Notice
                        [
                            {
                                MessageId = "label.requestDismissed"
                                Reason = None
                                Tone = NoticeTone.Info
                            }
                        ])
            ]

    let document (value: AccessRequestPage) =
        HumanDocument.listPage value.Basis (view value) value.NextCursor

    let dismissedDocument () =
        { HumanDocument.empty HumanDisclosure.Current with
            Screen = dismissed ()
        }
