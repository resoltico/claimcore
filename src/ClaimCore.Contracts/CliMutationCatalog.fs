namespace ClaimCore.Contracts

/// Only explicitly reviewed read-only requests may retain definite delivery failure.
/// New endpoints default to mutation-safe uncertainty until classified here.
[<RequireQualifiedAccess>]
module CliMutationCatalog =
    let private readOnlyIdentifiers =
        Set.ofList
            [
                "case.get"
                "case.list"
                "case.history"
                "operation.observe"
                "recovery.list"
                "recovery.inspect"
                "recovery.importEnvelopePreview"
                "authority.observe"
                "authority.reviewRealDataActivation"
                "lifecycle.review"
                "tombstone.review"
            ]

    let isMutation identifier =
        not (Set.contains identifier readOnlyIdentifiers)
