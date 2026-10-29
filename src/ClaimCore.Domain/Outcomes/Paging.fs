namespace ClaimCore.Domain

module Paging =
    [<Literal>]
    let DefaultPageSize = 50

    [<Literal>]
    let MaximumPageSize = 50

    [<Literal>]
    let MaximumCaseCursorCharacters = 16384
