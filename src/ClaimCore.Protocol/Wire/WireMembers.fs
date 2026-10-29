namespace ClaimCore.Protocol

/// Technical request member names have one wire-contract owner.
module WireMembers =
    [<Literal>]
    let Kind = "kind"

    [<Literal>]
    let OperationId = "operationId"

    [<Literal>]
    let ExpectedRevision = "expectedRevision"

    [<Literal>]
    let Act = "act"

    [<Literal>]
    let Values = "values"

    [<Literal>]
    let Groups = "groups"

    [<Literal>]
    let Reason = "reason"

    [<Literal>]
    let Target = "target"

    [<Literal>]
    let Issuer = "issuer"

    [<Literal>]
    let Subject = "subject"

    [<Literal>]
    let Role = "role"

    [<Literal>]
    let Scope = "scope"

    [<Literal>]
    let ActorId = "actorId"

    [<Literal>]
    let Detail = "detail"

    [<Literal>]
    let Cursor = "cursor"

    [<Literal>]
    let Limit = "limit"

/// Named codec definitions are used by the codec and its semantic annotations.
module RequestSchemas =
    [<Literal>]
    let Registration = "Registration"

    [<Literal>]
    let Decision = "Decision"

    [<Literal>]
    let Payment = "Payment"

    [<Literal>]
    let RegistrationGroup = "RegistrationGroup"

    [<Literal>]
    let DecisionGroup = "DecisionGroup"

    [<Literal>]
    let PaymentGroup = "PaymentGroup"

    [<Literal>]
    let Correction = "Correction"

    [<Literal>]
    let CaseAct = "CaseAct"

    [<Literal>]
    let CaseRequest = "CaseRequest"

    [<Literal>]
    let PrincipalInput = "PrincipalInput"

    [<Literal>]
    let Scope = "Scope"

    [<Literal>]
    let AuthorityAct = "AuthorityAct"

    [<Literal>]
    let AuthorityRequest = "AuthorityRequest"

    [<Literal>]
    let OperationInput = "OperationInput"

    [<Literal>]
    let CaseGetRequest = "CaseGetRequest"

    [<Literal>]
    let PageRequest = "PageRequest"

    [<Literal>]
    let CaseListRequest = "CaseListRequest"

    [<Literal>]
    let HistoryRequest = "HistoryRequest"

    [<Literal>]
    let ObserveRequest = "ObserveRequest"

    [<Literal>]
    let ResolveRequest = "ResolveRequest"

    [<Literal>]
    let GrantsRequest = "GrantsRequest"

    [<Literal>]
    let Empty = "Empty"
