namespace ClaimCore.RecordFormat

/// Identifies the complete grammar of a fresh installation, including its ledger and payloads.
module InstallationFormat =
    [<Literal>]
    let Current = 12

    /// Binds stores and ledger roots to this grammar independently of public format numbering.
    [<Literal>]
    let Identity = "claimcore-format-12-payment-corrections"
