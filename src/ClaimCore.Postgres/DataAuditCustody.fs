namespace ClaimCore.Postgres

open System.Threading
open Npgsql
open ClaimCore.Witness

/// Independently verifies owner-key authority, signed owner copies and product exports.
module internal DataAuditCustody =
    let verify
        (connection: NpgsqlConnection)
        (transaction: NpgsqlTransaction)
        (witness: WitnessProtocol)
        cutoff
        (ct: CancellationToken)
        =
        task {
            let! signerApprovals, signerKeys, signerEvents =
                DataAuditSignerEvents.verifyAll connection transaction witness cutoff ct

            let! ownerCopies =
                DataAuditOwnerCopies.verify connection transaction witness cutoff ct

            let! physicalVerifications =
                DataAuditCopyPhysicalVerifications.verify connection transaction witness cutoff ct

            let! deletionApprovals =
                DataAuditCopyDeletionApprovals.verify connection transaction witness cutoff ct

            let! _adoptionApprovals =
                DataAuditCopyAdoptionApprovals.verify connection transaction witness cutoff ct

            let! _externalPublications =
                DataAuditExternalPublications.verifyAll connection transaction witness cutoff ct

            let! _adoptions =
                DataAuditCopyAdoptions.verifyAll connection transaction witness cutoff ct

            let! _productCopyChains =
                DataAuditProductExportEventChain.verify connection transaction ct

            let! handoffApprovals =
                DataAuditWriterHandoffApprovals.verify connection transaction witness cutoff ct

            let! exports = DataAuditManagedCopies.verify connection transaction witness cutoff ct

            return
                signerApprovals,
                signerKeys,
                signerEvents,
                ownerCopies,
                physicalVerifications,
                deletionApprovals,
                handoffApprovals,
                exports
        }
