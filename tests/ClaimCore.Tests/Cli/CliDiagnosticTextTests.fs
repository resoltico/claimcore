module ClaimCore.Tests.CliDiagnosticTextTests

open ClaimCore.Contract.Protocol

open System.Text
open Expecto
open ClaimCore.Contract
open ClaimCore.Domain
open ClaimCore.Cli

let private frame refusal =
    {
        Endpoint = Some(EndpointId.Actor ActorEndpoint.CaseGet)
        OperationId = None
        Delivery = DeliveryState.Delivered
        Result =
            FrameResult.Reply(
                ClaimCore.Tests.CliReplies.ofValue Api.caseGet (ReadReply.Rejected refusal)
            )
    }

let private assertTarget target identity violation code parameters =
    let refusal = Refusal.InvalidInput(target, violation)
    let source = Frames.encode (frame refusal)
    let original = Array.copy source

    let parameters = Map.ofList parameters

    for language in LocalizationPolicy.languages do
        let rendered = TextReply.bytes language (frame refusal) |> Encoding.UTF8.GetString
        let label = MessageCatalogs.render language identity Map.empty

        let message = MessageCatalogs.render language ("diagnostic." + code) parameters

        Expect.equal
            (rendered.Split('\n')[0])
            (label + ": " + message)
            "The exact localized target distinguishes the affected input"

        Expect.equal source original "Human rendering preserves the structured reply bytes"

        Expect.equal
            (Frames.decode source)
            (Ok(frame refusal))
            "Structured diagnostic target and typed parameters remain unchanged"

let private requiredNames () =
    for field, identity in
        [
            BusinessField.ClaimantName, "field.claimantName.label"
            BusinessField.InsurerName, "field.insurerName.label"
        ] do
        assertTarget (InputTarget.Field field) identity Violation.TextRequired "TEXT_REQUIRED" []

let private dates () =
    for field, identity in
        [
            BusinessField.IncidentDate, "field.incidentDate.label"
            BusinessField.IncidentNotificationDate, "field.incidentNotificationDate.label"
        ] do
        assertTarget
            (InputTarget.Field field)
            identity
            Violation.CalendarDateRequired
            "CALENDAR_DATE_REQUIRED"
            []

let private principalAndPayment () =
    assertTarget
        InputTarget.PrincipalIssuer
        "input.issuer.label"
        Violation.PrincipalFormat
        "PRINCIPAL_FORMAT"
        []

    assertTarget
        InputTarget.PrincipalSubject
        "input.subject.label"
        Violation.TextRequired
        "TEXT_REQUIRED"
        []

    assertTarget
        (InputTarget.Group RecordGroup.Payment)
        "group.payment.label"
        Violation.PaymentClearRequired
        "PAYMENT_CLEAR_REQUIRED"
        []

let private remainingTargets () =
    assertTarget
        InputTarget.OperationId
        "input.operationId.label"
        Violation.OperationIdRequired
        "OPERATION_ID_REQUIRED"
        []

    assertTarget
        InputTarget.ExpectedRevision
        "input.expectedRevision.label"
        Violation.ExpectedRevisionRange
        "EXPECTED_REVISION_RANGE"
        []

    assertTarget InputTarget.Reason "input.reason.label" Violation.TextRequired "TEXT_REQUIRED" []

    assertTarget
        InputTarget.Limit
        "input.limit.label"
        (Violation.PageLimitRange 50)
        "PAGE_LIMIT_RANGE"
        [ "maximumPageSize", "50" ]

    assertTarget
        (InputTarget.Field BusinessField.ClaimantName)
        "field.claimantName.label"
        (Violation.TooLong 200)
        "TOO_LONG"
        [ "maximumCharacters", "200" ]

let private assertOptionalTarget refusal identity code =
    let source = Frames.encode (frame refusal)

    for language in LocalizationPolicy.languages do
        let rendered = TextReply.bytes language (frame refusal) |> Encoding.UTF8.GetString

        let message = MessageCatalogs.render language ("diagnostic." + code) Map.empty

        let expected =
            identity
            |> Option.map (fun key ->
                MessageCatalogs.render language key Map.empty + ": " + message)
            |> Option.defaultValue message

        Expect.equal
            (rendered.Split('\n')[0])
            expected
            "Only actual diagnostic targets add a localized label"

        Expect.equal
            (Frames.decode source)
            (Ok(frame refusal))
            "The structured diagnostic is unchanged"

[<Tests>]
let tests =
    testList
        "CLI diagnostic target text"
        [
            testCase
                "required claimant and insurer are distinguished in every language"
                requiredNames
            testCase "incident and notification dates are distinguished in every language" dates
            testCase
                "principal inputs and payment group use existing localized identities"
                principalAndPayment
            testCase
                "operation revision reason page and limit parameters retain their identity"
                remainingTargets
            testCase "cursor uses a localized identity" (fun () ->
                assertOptionalTarget
                    Refusal.InvalidCursor
                    (Some "input.cursor.label")
                    "INVALID_CURSOR")
            testCase "targetless refusal has no invented target" (fun () ->
                assertOptionalTarget Refusal.ResourceUnavailable None "RESOURCE_UNAVAILABLE")
        ]
