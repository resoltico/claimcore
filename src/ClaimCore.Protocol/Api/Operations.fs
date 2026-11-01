namespace ClaimCore.Protocol

open ClaimCore.Domain
open ClaimCore.Application.Contracts

[<RequireQualifiedAccess>]
type OperationEndpoint =
    | Preview
    | Execute

[<RequireQualifiedAccess>]
type QueryEndpoint =
    | CaseGet
    | CaseList
    | CaseSummary
    | CaseHistory
    | Observe
    | Status
    | Resolve
    | AuthorityList
    | AuthorityGrants
    | AuthorityHistory
    | Context

[<RequireQualifiedAccess>]
type ActorEndpoint =
    | Operation of OperationEndpoint
    | Query of QueryEndpoint

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module ActorEndpoint =
    // This total endpoint-token table owns stable public spellings and has no nested decisions; every closed endpoint remains compiler-checked.
    // fsharplint:disable-next-line CyclomaticComplexity
    let vocabulary =
        Vocabulary.define
            "endpoint"
            [
                (ActorEndpoint.Operation OperationEndpoint.Preview)
                (ActorEndpoint.Operation OperationEndpoint.Execute)
                (ActorEndpoint.Query QueryEndpoint.CaseGet)
                (ActorEndpoint.Query QueryEndpoint.CaseList)
                (ActorEndpoint.Query QueryEndpoint.CaseSummary)
                (ActorEndpoint.Query QueryEndpoint.CaseHistory)
                (ActorEndpoint.Query QueryEndpoint.Observe)
                (ActorEndpoint.Query QueryEndpoint.Status)
                (ActorEndpoint.Query QueryEndpoint.Resolve)
                (ActorEndpoint.Query QueryEndpoint.AuthorityList)
                (ActorEndpoint.Query QueryEndpoint.AuthorityGrants)
                (ActorEndpoint.Query QueryEndpoint.AuthorityHistory)
                (ActorEndpoint.Query QueryEndpoint.Context)
            ]
            (function
             | (ActorEndpoint.Operation OperationEndpoint.Preview) -> "operation.preview"
             | (ActorEndpoint.Operation OperationEndpoint.Execute) -> "operation.execute"
             | (ActorEndpoint.Query QueryEndpoint.CaseGet) -> "case.get"
             | (ActorEndpoint.Query QueryEndpoint.CaseList) -> "case.list"
             | (ActorEndpoint.Query QueryEndpoint.CaseSummary) -> "case.summary"
             | (ActorEndpoint.Query QueryEndpoint.CaseHistory) -> "case.history"
             | (ActorEndpoint.Query QueryEndpoint.Observe) -> "operation.observe"
             | (ActorEndpoint.Query QueryEndpoint.Status) -> "operation.status"
             | (ActorEndpoint.Query QueryEndpoint.Resolve) -> "operation.resolve"
             | (ActorEndpoint.Query QueryEndpoint.AuthorityList) -> "authority.list"
             | (ActorEndpoint.Query QueryEndpoint.AuthorityGrants) -> "authority.grants"
             | (ActorEndpoint.Query QueryEndpoint.AuthorityHistory) -> "authority.history"
             | (ActorEndpoint.Query QueryEndpoint.Context) -> "context")

    let all = Vocabulary.all vocabulary
    let token = Vocabulary.token vocabulary
    let parse = Vocabulary.tryParse vocabulary
