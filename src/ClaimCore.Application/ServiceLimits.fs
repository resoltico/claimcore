namespace ClaimCore.Application

/// Resource budgets are independent of business admission and durable format identities.
module ServiceLimits =
    [<Literal>]
    let DefaultPageSize = 50

    [<Literal>]
    let MaximumPageSize = 50

    [<Literal>]
    let RequestByteLimit = 65536

    [<Literal>]
    let RecoveryRetentionDays = 7

    [<Literal>]
    let RecoveryPerActor = 100

    [<Literal>]
    let RecoveryInstallation = 10000

    [<Literal>]
    let RecoveryCleanupBatch = 100
