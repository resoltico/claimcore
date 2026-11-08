namespace ClaimCore.Application.Contracts

open ClaimCore.Domain

module Paging =
    [<Literal>]
    let DefaultPageSize = 50

    [<Literal>]
    let MaximumPageSize = 50

    [<Literal>]
    let MaximumCaseCursorCharacters = 16384

    [<Literal>]
    let MaximumAccessRequestCursorCharacters = 32768

    [<Literal>]
    let MaximumActorCursorCharacters = 512

    /// Full timeline values and generic detail views must fit the bounded response envelope.
    [<Literal>]
    let MaximumTimelinePageSize = 10
