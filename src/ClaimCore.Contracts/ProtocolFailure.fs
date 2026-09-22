namespace ClaimCore.Contracts

open System

[<RequireQualifiedAccess>]
type ProtocolMember =
    | ProtocolVersion
    | Endpoint
    | Input
    | TimeoutMs
    | OperationId
    | CaseReference
    | ExpectedRevision
    | Command
    | Kind
    | Values
    | Groups
    | Registration
    | Decision
    | Payment
    | Mode
    | Cursor
    | Limit
    | Detail
    | View
    | AttemptCursor
    | AttemptLimit
    | RequestSha256
    | Confirmed
    | Destination
    | Source
    | SourceSha256
    | IncidentDate
    | IncidentNotificationDate
    | IncidentCountry
    | ClaimantName
    | InsurerName
    | ClaimedAmount
    | ClaimedCurrency
    | PaymentDecisionDate
    | PayableAmount
    | PayableCurrency
    | PaymentDate
    | Status

/// A location can contain declared member tokens only, never an unknown input key.
type ProtocolLocation = private ProtocolLocation of ProtocolMember list

module ProtocolLocation =
    let private members0 =
        [
            ProtocolMember.ProtocolVersion, "protocolVersion"
            ProtocolMember.Endpoint, "endpoint"
            ProtocolMember.Input, "input"
            ProtocolMember.TimeoutMs, "timeoutMs"
            ProtocolMember.OperationId, "operationId"
            ProtocolMember.CaseReference, "caseReference"
            ProtocolMember.ExpectedRevision, "expectedRevision"
            ProtocolMember.Command, "command"
            ProtocolMember.Kind, "kind"
            ProtocolMember.Values, "values"
        ]

    let private members1 =
        [
            ProtocolMember.Groups, "groups"
            ProtocolMember.Registration, "registration"
            ProtocolMember.Decision, "decision"
            ProtocolMember.Payment, "payment"
            ProtocolMember.Mode, "mode"
            ProtocolMember.Cursor, "cursor"
            ProtocolMember.Limit, "limit"
            ProtocolMember.Detail, "detail"
            ProtocolMember.View, "view"
            ProtocolMember.AttemptCursor, "attemptCursor"
        ]

    let private members2 =
        [
            ProtocolMember.AttemptLimit, "attemptLimit"
            ProtocolMember.RequestSha256, "requestSha256"
            ProtocolMember.Confirmed, "confirmed"
            ProtocolMember.Destination, "destination"
            ProtocolMember.Source, "source"
            ProtocolMember.SourceSha256, "sourceSha256"
            ProtocolMember.IncidentDate, "incidentDate"
            ProtocolMember.IncidentNotificationDate, "incidentNotificationDate"
            ProtocolMember.IncidentCountry, "incidentCountry"
            ProtocolMember.ClaimantName, "claimantName"
        ]

    let private members3 =
        [
            ProtocolMember.InsurerName, "insurerName"
            ProtocolMember.ClaimedAmount, "claimedAmount"
            ProtocolMember.ClaimedCurrency, "claimedCurrency"
            ProtocolMember.PaymentDecisionDate, "paymentDecisionDate"
            ProtocolMember.PayableAmount, "payableAmount"
            ProtocolMember.PayableCurrency, "payableCurrency"
            ProtocolMember.PaymentDate, "paymentDate"
            ProtocolMember.Status, "status"
        ]

    let private members = members0 @ members1 @ members2 @ members3
    let root = ProtocolLocation []

    // Adapter-authored pointers are admitted to a closed vocabulary. Unknown keys and array
    // positions deliberately collapse to root rather than becoming diagnostic payload.
    let fromPath (path: string) =
        if String.IsNullOrEmpty path then
            root
        else
            let tokens = path.Split('/') |> Array.toList

            let parsed =
                tokens.Tail |> List.map (fun name -> members |> List.tryFind (snd >> (=) name))

            if tokens.Head <> "" || parsed.Length > 8 || parsed |> List.exists Option.isNone then
                root
            else
                ProtocolLocation(parsed |> List.choose (Option.map fst))

    let value (ProtocolLocation parts) =
        parts
        |> List.map (fun part -> "/" + (members |> List.find (fst >> (=) part) |> snd))
        |> String.concat ""

    let pattern =
        "^(?:/(?:" + (members |> List.map snd |> String.concat "|") + ")){0,8}$"

/// Reason and safe location are independent of presentation copy.
type ProtocolFailure =
    private
        {
            ReasonValue: ProtocolProblem
            LocationValue: ProtocolLocation
        }

    member this.Reason = this.ReasonValue
    member this.Code = ProtocolProblems.code this.ReasonValue
    member this.Path = ProtocolLocation.value this.LocationValue

module ProtocolFailure =
    let create reason location =
        {
            ReasonValue = reason
            LocationValue = location
        }
