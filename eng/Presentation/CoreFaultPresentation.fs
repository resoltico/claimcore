#if CONTRACT_GENERATION
namespace ClaimCore.ContractGeneration.Presentation
#else
namespace ClaimCore.Hosting.Presentation
#endif

open ClaimCore.Catalog

open ClaimCore.Application

module internal CoreFaultPresentation =
    let render =
        function
        | CoreFault.InternalError -> (MessageText.literal "diagnostic.CORE_INTERNAL_ERROR")
        | CoreFault.StoreUnavailable -> (MessageText.literal "diagnostic.CORE_STORE_UNAVAILABLE")
        | CoreFault.StoreIntegrityError ->
            (MessageText.literal "diagnostic.CORE_STORE_INTEGRITY_ERROR")
        | CoreFault.SchemaMismatch -> (MessageText.literal "diagnostic.CORE_SCHEMA_MISMATCH")
        | CoreFault.OperationContentConflict ->
            (MessageText.literal "diagnostic.CORE_OPERATION_CONTENT_CONFLICT")
