namespace ClaimCore.Presentation

open System
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ScreenEntries

module AccessRequestsScreen =
    let accessRequestColumns =
        [
            column
                "nameHint"
                "label.requestedName"
                Prominence.Primary
                false
                (fun (item: AccessRequest) -> optional PresentedValue.Party item.Claims.Name)
            column "usernameHint" "label.requestedUsername" Prominence.Secondary false (fun item ->
                optional PresentedValue.Party item.Claims.PreferredUsername)
            column "emailHint" "label.requestedEmail" Prominence.Secondary false (fun item ->
                optional PresentedValue.Party item.Claims.Email)
            column
                "principalKind"
                "field.principal.kind.label"
                Prominence.Secondary
                false
                (fun item ->
                    let kind, _, _ = PrincipalKey.parts item.Principal in
                    code PrincipalKind.vocabulary kind)
            column "issuer" "field.principal.issuer.label" Prominence.Detail false (fun item ->
                let _, issuer, _ = PrincipalKey.parts item.Principal in
                PresentedValue.Identifier issuer)
            column "subject" "field.principal.subject.label" Prominence.Detail false (fun item ->
                let _, _, subject = PrincipalKey.parts item.Principal in
                PresentedValue.Identifier subject)
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
        resultScreen
            (fun (row: AccessRequest) -> principalKey row.Principal)
            accessRequestColumns
            rows

    let view (reply: AccessRequestPage) =
        page reply.Basis reply.Items.Length reply.NextCursor (rows reply.Items)

    let dismissed () =
        Screen.create
            [
                region
                    RegionRole.Notice
                    None
                    (RegionContent.Notice
                        {
                            MessageId = "label.requestDismissed"
                            Reason = None
                            Tone = NoticeTone.Info
                        })
            ]
