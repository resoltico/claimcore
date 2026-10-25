module ClaimCore.Tests.CliDiagnosticTextTests

open System.Text
open Expecto
open ClaimCore.Contract
open ClaimCore.Cli

let private frame refusal =
    {
        Endpoint = Some Api.caseGet.Id
        OperationId = None
        Delivery = DeliveryState.Delivered
        Result = FrameResult.Reply(Api.caseGet.Output.Write(ReadReply.Rejected refusal))
    }

let private assertTarget target identity violation =
    let refusal = Refusal.InvalidInput(target, violation)
    let source = Frames.encode (frame refusal)
    let original = Array.copy source

    let parameters =
        Refusals.parameters refusal
        |> List.map (fun (key, value) -> key, string value)
        |> Map.ofList

    for language in LocalizationPolicy.languages do
        let rendered = TextReply.bytes language source |> Encoding.UTF8.GetString
        let label = MessageCatalogs.render language identity Map.empty

        let message =
            MessageCatalogs.render language ("diagnostic." + (Refusals.row refusal).Code) parameters

        Expect.equal
            (rendered.Split('\n')[0])
            (label + ": " + message)
            "The exact localized target distinguishes the affected input"

        Expect.equal source original "Human rendering preserves the structured reply bytes"

        Expect.equal
            (Frames.decode source)
            (Ok(frame refusal))
            "Structured diagnostic target and limits remain unchanged"

let private requiredNames () =
    for field, identity in
        [
            BusinessField.ClaimantName, "field.claimantName.label"
            BusinessField.InsurerName, "field.insurerName.label"
        ] do
        assertTarget (InputTarget.Field field) identity Violation.TextRequired

let private dates () =
    for field, identity in
        [
            BusinessField.IncidentDate, "field.incidentDate.label"
            BusinessField.IncidentNotificationDate, "field.incidentNotificationDate.label"
        ] do
        assertTarget (InputTarget.Field field) identity Violation.CalendarDateRequired

let private principalAndPayment () =
    assertTarget InputTarget.PrincipalIssuer "input.issuer.label" Violation.PrincipalFormat

    assertTarget InputTarget.PrincipalSubject "input.subject.label" Violation.TextRequired
    assertTarget InputTarget.PaymentGroup "group.payment.label" Violation.PaymentClearRequired

let private remainingTargets () =
    assertTarget InputTarget.OperationId "input.operationId.label" Violation.OperationIdRequired

    assertTarget
        InputTarget.ExpectedRevision
        "input.expectedRevision.label"
        Violation.ExpectedRevisionRange

    assertTarget InputTarget.Reason "input.reason.label" Violation.TextRequired
    assertTarget InputTarget.Limit "input.limit.label" (Violation.PageLimitRange 50)

    assertTarget
        (InputTarget.Field BusinessField.ClaimantName)
        "field.claimantName.label"
        (Violation.TooLong 200)

let private assertOptionalTarget refusal identity =
    let source = Frames.encode (frame refusal)

    for language in LocalizationPolicy.languages do
        let rendered = TextReply.bytes language source |> Encoding.UTF8.GetString

        let message =
            MessageCatalogs.render language ("diagnostic." + (Refusals.row refusal).Code) Map.empty

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
                assertOptionalTarget Refusal.InvalidCursor (Some "input.cursor.label"))
            testCase "targetless refusal has no invented target" (fun () ->
                assertOptionalTarget Refusal.ResourceUnavailable None)
        ]
