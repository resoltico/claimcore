#if CONTRACT_GENERATION
namespace ClaimCore.ContractGeneration.Presentation
#else
namespace ClaimCore.Hosting.Presentation
#endif

open ClaimCore.Application

module internal CoreFaultPresentation =
    let render =
        function
        | CoreFault.InternalError -> "An internal error occurred. Stop and investigate."
        | CoreFault.StoreUnavailable -> "The store is unavailable. Retry."
        | CoreFault.StoreIntegrityError -> "Stored data failed verification. Stop and run verify."
        | CoreFault.SchemaMismatch ->
            "The schema differs from this build. Run migrate or use a matching build."
        | CoreFault.OperationContentConflict ->
            "Stored operation content is inconsistent. Stop and investigate."
