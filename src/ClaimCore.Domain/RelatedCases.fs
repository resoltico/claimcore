namespace ClaimCore.Domain

open System

[<Struct>]
type LinkId = private LinkId of Guid

module LinkId =
    let create value =
        if value = Guid.Empty then None else Some(LinkId value)

    let ofOperationId operation = LinkId(OperationId.value operation)
    let value (LinkId value) = value
    let format id = (value id).ToString("D")

type RelatedCaseContent =
    {
        Target: CaseId
        Note: NoteText option
    }

module RelatedCase =
    [<Literal>]
    let MaximumCurrent = 20

    [<Literal>]
    let MaximumIncomingShown = 20
