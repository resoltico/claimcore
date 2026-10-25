namespace ClaimCore.Application

/// Resource budgets are independent of business admission and durable format identities.
module ServiceLimits =
    [<Literal>]
    let DefaultPageSize = 50

    [<Literal>]
    let MaximumPageSize = 50

    [<Literal>]
    let RequestByteLimit = 65536
