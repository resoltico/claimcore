namespace ClaimCore.Application

/// Compatibility-free internal route to the focused typed workflow modules. Keeping this tiny
/// module makes composition readable without turning it into a second behavior owner.
module internal TypedWorkflow =
    let prepare = TypedPreparation.prepare
    let execute = TypedSubmission.execute
    let get = TypedQueries.get
    let list = TypedQueries.list
    let history = TypedQueries.history
    let observe = TypedQueries.observe
    let resolveRetained = TypedResolution.resolveRetained
