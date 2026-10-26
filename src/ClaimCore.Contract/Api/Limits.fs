namespace ClaimCore.Contract

module Limits =
    [<Literal>]
    let DefaultPageSize = 50

    [<Literal>]
    let MaximumRequestBytes = 65536

    [<Literal>]
    let MaximumInvocationBytes = 131072

    [<Literal>]
    let MaximumResponseBytes = 4194304

    [<Literal>]
    let MaximumPageSize = 50
