namespace ClaimCore.Cli

open System
open System.Globalization
open System.Text
open ClaimCore.Protocol
open ClaimCore.Domain
open ClaimCore.Json

/// Human output consumes decoded DTOs and the authoritative field readers.
module internal TextReply =
    let private label language key =
        MessageCatalogs.render language key Map.empty

    let private term language vocabulary value =
        label language (Vocabulary.messageKey vocabulary value "label")

    let private principalKind language value =
        Vocabulary.tryParse PrincipalKind.vocabulary value
        |> Option.map (term language PrincipalKind.vocabulary)
        |> Option.defaultValue value

    let private line language key value = label language key + ": " + value

    let private number (value: int64) =
        value.ToString(CultureInfo.InvariantCulture)

    let private id (value: Guid) = value.ToString "D"
    let private boolean value = if value then "true" else "false"

    let private instant (value: DateTimeOffset) =
        value.ToUniversalTime().ToString(JsonFormats.InstantFormat, CultureInfo.InvariantCulture)

    let private json codec value =
        Codec.encode codec value |> Encoding.UTF8.GetString

    let private displayed language =
        function
        | None -> "—"
        | Some text ->
            let warning = TextDisplay.warning text

            if warning.CodePoints.IsEmpty then
                text
            else
                let points =
                    warning.CodePoints @ (if warning.Truncated then [ "…" ] else [])
                    |> String.concat ", "

                text
                + " ["
                + MessageCatalogs.renderCli language (CliMessage.CharacterWarning points)
                + "]"

    let private fieldLines language fields =
        let rows placement =
            CaseFields.declarations
            |> List.filter (fun row -> row.Group = placement)
            |> List.map (fun row ->
                line
                    language
                    (FieldPresentation.labelId row.Field)
                    (displayed
                        language
                        (if row.Field = BusinessField.Status then
                             Some(term language CaseStatus.vocabulary fields.Status)
                         else
                             BusinessField.value fields row.Field)))

        rows None
        @ (Vocabulary.all RecordGroup.vocabulary
           |> List.collect (fun group ->
               (label language (Vocabulary.messageKey RecordGroup.vocabulary group "label")
                + ":")
               :: rows (Some group)))

    let private offerAct language act =
        term language CaseActKind.vocabulary act

    let private caseLines language (value: CaseReadModel) =
        [
            line
                language
                (FieldPresentation.labelId BusinessField.CaseReference)
                (CaseReference.format value.Snapshot.Reference)
            line
                language
                (CliMessages.key CliMessage.RevisionLabel)
                (number value.Snapshot.Revision)
            line
                language
                "cli.text.state"
                (term language Lifecycle.vocabulary (CaseState.lifecycle value.Snapshot.State))
        ]
        @ (CaseState.fields value.Snapshot.State
           |> Option.map (fieldLines language)
           |> Option.defaultValue [])
        @ [
            line
                language
                (CliMessages.key CliMessage.AvailableActsLabel)
                (value.Advice.Acts |> List.map (offerAct language) |> String.concat ", ")
        ]

    let private receiptLines language outcome (receipt: OperationReceipt) =
        let act, target, revision =
            match receipt.Subject with
            | ReceiptSubject.Case value ->
                term language CaseActKind.vocabulary value.Act,
                CaseReference.format value.Reference,
                [
                    label language (CliMessages.key CliMessage.RevisionLabel)
                    number value.Revision
                ]
            | ReceiptSubject.Authority value ->
                term language AuthorityKind.vocabulary value.Act, id value.TargetActorId, []

        [
            String.concat
                " "
                ([ outcome; act; target ]
                 @ revision
                 @ [
                     label
                         language
                         (ContractDiscovery.inputLabel (
                             InteractionInput.Target InputTarget.OperationId
                         ))
                     id receipt.OperationId
                 ])
        ]

    let private nextLines language =
        function
        | None -> []
        | Some value -> [ line language "cli.text.next" value ]

    let private historyLines language (page: HistoryResultPage) =
        (page.Entries
         |> List.collect (function
             | HistoryEntry.SummaryEntry change ->
                 [
                     String.concat
                         "  "
                         [
                             number change.Revision
                             term language CaseActKind.vocabulary change.Act
                             instant change.RecordedAt
                             id change.OperationId
                         ]
                 ]
             | HistoryEntry.FullEntry receipt ->
                 let revision, act, fields =
                     match receipt.Subject with
                     | ReceiptSubject.Case value ->
                         let fields =
                             match value.Snapshot with
                             | SnapshotDisclosure.Disclosed live ->
                                 LiveCase.toState live
                                 |> CaseState.fields
                                 |> Option.map (fieldLines language)
                                 |> Option.defaultValue []
                             | SnapshotDisclosure.Withheld
                             | SnapshotDisclosure.Erased -> []

                         number value.Revision,
                         term language CaseActKind.vocabulary value.Act,
                         fields
                     | ReceiptSubject.Authority value ->
                         "—", term language AuthorityKind.vocabulary value.Act, []

                 String.concat
                     "  "
                     [ revision; act; instant receipt.RecordedAt; id receipt.OperationId ]
                 :: (fields |> List.map ((+) "  "))))
        @ nextLines language page.NextCursor

    let private grantRow language (grant: AuthorityGrant) =
        let scope, reference =
            match grant.Scope with
            | GrantTarget.Installation ->
                term language ScopeKind.vocabulary ScopeKind.Installation, "—"
            | GrantTarget.CaseReference reference ->
                term language ScopeKind.vocabulary ScopeKind.Case, reference

        String.concat "  " [ term language Role.vocabulary grant.Role; scope; reference ]

    let private actorRow language (actor: ActorSummary) =
        String.concat
            "  "
            [
                id actor.ActorId
                principalKind language actor.Principal.Kind
                actor.Principal.Issuer
                actor.Principal.Subject
                boolean actor.Enabled
            ]

    let private casePage language (page: CaseSummaryPage) =
        (page.Items
         |> List.map (fun item ->
             let status =
                 match item.State with
                 | CaseState.Active status
                 | CaseState.Voided status -> term language CaseStatus.vocabulary status
                 | CaseState.Erased -> "—"

             String.concat
                 "  "
                 [
                     CaseReference.format item.Reference
                     number item.Revision
                     term language Lifecycle.vocabulary (CaseState.lifecycle item.State)
                     + "  "
                     + status
                 ]))
        @ nextLines language page.NextCursor

    let private actorPage language (page: ActorReviewPage) =
        (page.Items |> List.map (actorRow language))
        @ nextLines language page.NextCursor

    let private grantPage language (page: AuthorityGrantPage) =
        (page.Items |> List.map (grantRow language))
        @ nextLines language page.NextCursor

    let private requestInput request =
        request
        |> Option.bind (fun invocation ->
            RequestCodecs.operationInput.Read "$" (Frames.input invocation)
            |> Result.toOption)

    let private reviewIdentity language request review =
        let input = requestInput request

        let act =
            input
            |> Option.map (function
                | OperationInput.Case value ->
                    term language CaseActKind.vocabulary (CaseAct.kind value.Act)
                | OperationInput.Authority value ->
                    term language AuthorityKind.vocabulary (AuthorityAct.kind value.Act))
            |> Option.defaultValue "—"

        let target =
            match review with
            | OperationReview.Case value -> CaseReference.format value.Proposed.Reference
            | OperationReview.Authority value ->
                let principal =
                    match input with
                    | Some(OperationInput.Authority value) -> Some value.Target
                    | _ -> value.Target |> Option.map _.Principal

                match principal with
                | Some value ->
                    String.concat
                        " "
                        [ principalKind language value.Kind; value.Issuer; value.Subject ]
                | None -> "— — —"

        act + " " + target

    let private authorityChanges language (before: AuthorityState) (after: AuthorityState) =
        [
            (CliMessages.key CliMessage.EnabledLabel), boolean before.Enabled, boolean after.Enabled
            "cli.text.grants",
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

    let private reviewLines language request review =
        let changes =
            match review with
            | OperationReview.Authority value -> authorityChanges language value.Before value.After
            | OperationReview.Case value ->
                let before =
                    value.Before |> Option.bind (fun snapshot -> CaseState.fields snapshot.State)

                let after = CaseState.fields value.Proposed.State

                CaseFields.declarations
                |> List.choose (fun row ->
                    let previous =
                        before |> Option.bind (fun fields -> BusinessField.value fields row.Field)

                    let next =
                        after |> Option.bind (fun fields -> BusinessField.value fields row.Field)

                    if previous = next then
                        None
                    else
                        Some(
                            line
                                language
                                (FieldPresentation.labelId row.Field)
                                (displayed language previous + " → " + displayed language next)
                        ))

        line
            language
            (CliMessages.key CliMessage.ReviewTitle)
            (reviewIdentity language request review)
        :: changes

    let private diagnosticTargetIdentity target =
        ContractDiscovery.inputLabel (InteractionInput.Target target)

    let private refusalLines language refusal =
        let code, parameters = DiagnosticCodecs.describe refusal

        let arguments =
            parameters
            |> List.map (fun (name, value) -> name, number (int64 value))
            |> Map.ofList

        let message = MessageCatalogs.render language ("diagnostic." + code) arguments

        let message =
            match Refusals.target refusal with
            | None -> message
            | Some target -> label language (diagnosticTargetIdentity target) + ": " + message

        [
            message
            line language "cli.text.code" code
            line
                language
                (CliMessages.key CliMessage.ActionLabel)
                (RecommendedAction.token refusal.Action)
        ]

    let private faultLines language fault =
        let code = CoreFault.token fault

        [
            label language ("fault." + code)
            line language "cli.text.code" code
            line
                language
                (CliMessages.key CliMessage.ActionLabel)
                (RecommendedAction.token fault.Action)
        ]

    let private readLines language success =
        function
        | ReadReply.Succeeded value -> success value
        | ReadReply.Rejected refusal -> refusalLines language refusal
        | ReadReply.Failed fault -> faultLines language fault

    let private contextLines (value: ActorContext) =
        [
            "productVersion: " + value.Runtime.ProductVersion
            "businessDate: "
            + value.Runtime.EffectiveBusinessDate.ToString(
                CalendarDate.ExactFormat,
                CultureInfo.InvariantCulture
            )
            "timeZone: " + value.Runtime.TimeZoneId
            "offers: "
            + json (Codec.list Int32.MaxValue ResponseCodecs.installationOffer) value.Offers
            "capabilities: "
            + json
                (Codec.list Int32.MaxValue (VocabularyCodec.create Capability.vocabulary))
                value.Capability
            "views: "
            + json (Codec.list Int32.MaxValue (VocabularyCodec.create View.vocabulary)) value.View
        ]

    let private sessionLines (value: Session) =
        [
            "authenticated: " + boolean value.Authenticated
            "contractIdentity: " + value.ContractIdentity
            "antiforgeryToken: " + Option.defaultValue "—" value.AntiforgeryToken
        ]

    let private previewLines language request (reply: PreviewReply) =
        match reply.Outcome with
        | PreviewResult.Reviewed review -> reviewLines language request review
        | PreviewResult.AlreadyAccepted receipt -> receiptLines language "ALREADY_ACCEPTED" receipt
        | PreviewResult.Rejected refusal -> refusalLines language refusal
        | PreviewResult.Failed fault -> faultLines language fault

    let private executeLines language (reply: ExecuteReply) =
        match reply.Outcome with
        | ExecuteResult.Accepted receipt -> receiptLines language "ACCEPTED" receipt
        | ExecuteResult.AlreadyAccepted receipt -> receiptLines language "ALREADY_ACCEPTED" receipt
        | ExecuteResult.Rejected refusal -> refusalLines language refusal
        | ExecuteResult.Failed fault -> faultLines language fault
        | ExecuteResult.OutcomeUnknown -> [ label language "cli.text.uncertain" ]

    let private resolveLines language (reply: ResolveReply) =
        match reply with
        | ResolveReply.Accepted receipt -> receiptLines language "ACCEPTED" receipt
        | ResolveReply.NotAccepted -> [ label language "notice.notAccepted" ]
        | ResolveReply.Rejected refusal -> refusalLines language refusal
        | ResolveReply.Failed fault -> faultLines language fault

    let private replyLines language request =
        function
        | ServiceReply.Session(_, reply) -> readLines language sessionLines reply
        | ServiceReply.Logout(_, reply) -> readLines language (fun () -> [ "—" ]) reply
        | ServiceReply.Preview(_, reply) -> previewLines language request reply
        | ServiceReply.Execute(_, reply) -> executeLines language reply
        | ServiceReply.CaseGet(_, reply) -> readLines language (caseLines language) reply
        | ServiceReply.CaseList(_, reply) -> readLines language (casePage language) reply
        | ServiceReply.CaseHistory(_, reply) -> readLines language (historyLines language) reply
        | ServiceReply.Observe(_, reply) ->
            readLines language (receiptLines language "ACCEPTED") reply
        | ServiceReply.Resolve(_, reply) -> resolveLines language reply
        | ServiceReply.AuthorityList(_, reply) -> readLines language (actorPage language) reply
        | ServiceReply.AuthorityGrants(_, reply) -> readLines language (grantPage language) reply
        | ServiceReply.Context(_, reply) -> readLines language contextLines reply

    let private resultLines language request =
        function
        | FrameResult.Reply reply -> replyLines language request reply
        | FrameResult.HostFailure failure ->
            [
                label language ("host." + failure.Code)
                line language "cli.text.code" failure.Code
            ]
        | FrameResult.LocalFailure(CliFailure.ConfigurationIssue(setting, issue)) ->
            let setting = InputDiagnostics.settingName setting

            let issue = ConfigurationIssue.token issue

            [
                MessageCatalogs.renderCli language (CliMessage.ConfigurationIssue(setting, issue))
            ]
        | FrameResult.LocalFailure failure ->
            let code = Frames.failureCode failure
            [ label language ("cli.failure." + code); line language "cli.text.code" code ]

    let bytesFor language request (frame: FrameReply) =
        let lines = resultLines language request frame.Result
        let warning = label language "cli.text.uncertain"

        let lines =
            if frame.Delivery = DeliveryState.Uncertain && not (List.contains warning lines) then
                lines @ [ warning ]
            else
                lines

        lines |> String.concat "\n" |> fun text -> Encoding.UTF8.GetBytes(text + "\n")

    let bytes language frame = bytesFor language None frame
