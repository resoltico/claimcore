namespace ClaimCore.RecordFormat

open ClaimCore.Domain

/// Permanent format names are permanent literals, independent of wire vocabulary and transport.
module DurableNames =
    let caseAct =
        function
        | CaseActKind.Open -> "OPEN"
        | CaseActKind.AmendRegistration -> "AMEND_REGISTRATION"
        | CaseActKind.Correct -> "CORRECT"
        | CaseActKind.Decide -> "DECIDE"
        | CaseActKind.WithdrawDecision -> "WITHDRAW_DECISION"
        | CaseActKind.RecordPayment -> "RECORD_PAYMENT"
        | CaseActKind.ClearPayment -> "CLEAR_PAYMENT"
        | CaseActKind.Close -> "CLOSE"
        | CaseActKind.Reopen -> "REOPEN"
        | CaseActKind.Void -> "VOID"
        | CaseActKind.Reinstate -> "REINSTATE"
        | CaseActKind.Erase -> "ERASE"

    let authorityAct =
        function
        | AuthorityKind.ProvisionOwner -> "PROVISION_OWNER"
        | AuthorityKind.Register -> "REGISTER"
        | AuthorityKind.Grant -> "GRANT"
        | AuthorityKind.Revoke -> "REVOKE"
        | AuthorityKind.Enable -> "ENABLE"
        | AuthorityKind.Disable -> "DISABLE"

    let progress =
        function
        | CaseProgress.Undecided -> "UNDECIDED"
        | CaseProgress.Decided -> "DECIDED"
        | CaseProgress.Paid -> "PAID"

    let caseOrder =
        function
        | CaseListOrder.Reference -> "REFERENCE"
        | CaseListOrder.Recent -> "RECENT"
        | CaseListOrder.Notified -> "NOTIFIED"

    let caseAssignment =
        function
        | CaseAssignment.AssignedToMe -> "ASSIGNED_TO_ME"

    let status =
        function
        | CaseStatus.Opened -> "OPENED"
        | CaseStatus.Closed -> "CLOSED"

    let lifecycle =
        function
        | Lifecycle.Active -> "ACTIVE"
        | Lifecycle.Voided -> "VOIDED"
        | Lifecycle.Erased -> "ERASED"

    let correction =
        function
        | CorrectionGroupAction.Keep -> "KEEP"
        | CorrectionGroupAction.Replace -> "REPLACE"
        | CorrectionGroupAction.Clear -> "CLEAR"

    let principalKind =
        function
        | PrincipalKind.Human -> "HUMAN"
        | PrincipalKind.Service -> "SERVICE"

    let role =
        function
        | Role.Owner -> "OWNER"
        | Role.CaseReader -> "CASE_READER"
        | Role.CaseRegistrar -> "CASE_REGISTRAR"
        | Role.CaseHandler -> "CASE_HANDLER"
        | Role.PaymentRecorder -> "PAYMENT_RECORDER"
        | Role.DataSteward -> "DATA_STEWARD"
        | Role.Auditor -> "AUDITOR"

    let group =
        function
        | RecordGroup.Registration -> "registration"
        | RecordGroup.Decision -> "decision"
        | RecordGroup.Payment -> "payment"

    // An exhaustive literal table protects permanent format names when the union grows.
    // fsharplint:disable-next-line CyclomaticComplexity
    let field =
        function
        | BusinessField.IncidentDate -> "incidentDate"
        | BusinessField.IncidentNotificationDate -> "incidentNotificationDate"
        | BusinessField.IncidentCountry -> "incidentCountry"
        | BusinessField.ClaimantName -> "claimantName"
        | BusinessField.InsurerName -> "insurerName"
        | BusinessField.ClaimedAmount -> "claimedAmount"
        | BusinessField.ClaimedCurrency -> "claimedCurrency"
        | BusinessField.PaymentDecisionDate -> "paymentDecisionDate"
        | BusinessField.PayableAmount -> "payableAmount"
        | BusinessField.PayableCurrency -> "payableCurrency"
        | BusinessField.PaymentDate -> "paymentDate"
        | BusinessField.CaseReference -> "caseReference"
        | BusinessField.Status -> "status"

    // A total table preserves the permanent spelling of every closed input target.
    // fsharplint:disable-next-line CyclomaticComplexity
    let inputTarget =
        function
        | InputTarget.Field value -> field value
        | InputTarget.Group value -> group value
        | InputTarget.OperationId -> "operationId"
        | InputTarget.ExpectedRevision -> "expectedRevision"
        | InputTarget.Reason -> "reason"
        | InputTarget.PrincipalKind -> "principal.kind"
        | InputTarget.PrincipalIssuer -> "principal.issuer"
        | InputTarget.PrincipalSubject -> "principal.subject"
        | InputTarget.Role -> "role"
        | InputTarget.Limit -> "limit"
        | InputTarget.Cursor -> "cursor"
        | InputTarget.Text -> "text"

    let caseKinds =
        [
            CaseActKind.Open
            CaseActKind.AmendRegistration
            CaseActKind.Correct
            CaseActKind.Decide
            CaseActKind.WithdrawDecision
            CaseActKind.RecordPayment
            CaseActKind.ClearPayment
            CaseActKind.Close
            CaseActKind.Reopen
            CaseActKind.Void
            CaseActKind.Reinstate
            CaseActKind.Erase
        ]

    let authorityKinds =
        [
            AuthorityKind.ProvisionOwner
            AuthorityKind.Register
            AuthorityKind.Grant
            AuthorityKind.Revoke
            AuthorityKind.Enable
            AuthorityKind.Disable
        ]

    let tryCaseAct token =
        caseKinds |> List.tryFind (fun value -> caseAct value = token)

    let tryAuthorityAct token =
        authorityKinds |> List.tryFind (fun value -> authorityAct value = token)

    let statusEntries =
        [ CaseStatus.Opened; CaseStatus.Closed ]
        |> List.map (fun value -> value, status value)

    let tryStatus token =
        statusEntries
        |> List.tryPick (fun (value, name) -> if name = token then Some value else None)

    let tryLifecycle token =
        [ Lifecycle.Active; Lifecycle.Voided; Lifecycle.Erased ]
        |> List.tryFind (fun value -> lifecycle value = token)

    let tryProgress token =
        [ CaseProgress.Undecided; CaseProgress.Decided; CaseProgress.Paid ]
        |> List.tryFind (fun value -> progress value = token)

    let principalKindEntries =
        [ PrincipalKind.Human; PrincipalKind.Service ]
        |> List.map (fun value -> value, principalKind value)

    let principalKinds = principalKindEntries |> List.map snd

    let tryPrincipalKind token =
        principalKindEntries
        |> List.tryPick (fun (value, name) -> if name = token then Some value else None)

    let roleEntries =
        [
            Role.Owner
            Role.CaseReader
            Role.CaseRegistrar
            Role.CaseHandler
            Role.PaymentRecorder
            Role.DataSteward
            Role.Auditor
        ]
        |> List.map (fun value -> value, role value)

    let tryRole token =
        roleEntries
        |> List.tryPick (fun (value, name) -> if name = token then Some value else None)

    [<Literal>]
    let Act = "act"

    [<Literal>]
    let Actor = "actor"

    [<Literal>]
    let After = "after"

    [<Literal>]
    let Case = "case"

    [<Literal>]
    let CaseId = "caseId"

    [<Literal>]
    let CaseReference = "caseReference"

    [<Literal>]
    let Decision = "decision"

    [<Literal>]
    let ExpectedRevision = "expectedRevision"

    [<Literal>]
    let Fields = "fields"

    [<Literal>]
    let Format = "format"

    [<Literal>]
    let Groups = "groups"

    [<Literal>]
    let Issuer = "issuer"

    [<Literal>]
    let Kind = "kind"

    [<Literal>]
    let OperationId = "operationId"

    [<Literal>]
    let PayloadCommitment = "payloadCommitment"

    [<Literal>]
    let Payment = "payment"

    [<Literal>]
    let PreviousHash = "previousHash"

    [<Literal>]
    let Reason = "reason"

    [<Literal>]
    let Registration = "registration"

    [<Literal>]
    let Revision = "revision"

    [<Literal>]
    let Role = "role"

    [<Literal>]
    let Scope = "scope"

    [<Literal>]
    let State = "state"

    [<Literal>]
    let Status = "status"

    [<Literal>]
    let Subject = "subject"

    [<Literal>]
    let Target = "target"

    [<Literal>]
    let Values = "values"

    [<Literal>]
    let V = "v"

    [<Literal>]
    let CaseSubject = "CASE"

    [<Literal>]
    let AuthoritySubject = "AUTHORITY"

    [<Literal>]
    let InstallationScope = "INSTALLATION"

    [<Literal>]
    let CaseScope = "CASE"
