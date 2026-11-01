namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

type SupportedRecordFormats =
    {
        CaseRequest: int
        Snapshot: int
        Authority: int
        Control: int
        LedgerEntry: int
    }

/// A read snapshot, not a history verification or an all-process drain assertion.
type InstallationStatus =
    {
        InstallationId: Guid
        Baseline: string
        BaselineSha256: string
        BusinessTimeZone: string
        ObservedAt: DateTimeOffset
        Position: LedgerPosition
        Maintenance: RegisterMaintenance
        Formats: SupportedRecordFormats
        Actors: int64
        Cases: int64
        Fences: int64
        Sessions: int64
        ExpiredSessions: int64
    }

module internal InstallationStatus =
    let read (source: NpgsqlDataSource) token =
        DatabaseSession.read source token (fun connection ->
            task {
                ServerSupport.require connection.PostgreSqlVersion
                let! installation, zone = Baseline.require connection token
                let! _ = Transactions.configure connection Transactions.timeouts token
                let! instant = SessionRows.now connection token
                let! mode = RegisterMaintenanceStore.read connection token

                let! rows =
                    DatabaseSession.rows
                        connection
                        ("SELECT sequence,entry_hash,(SELECT count(*) FROM claimcore.actors),(SELECT count(*) FROM claimcore.cases),(SELECT count(*) FROM claimcore.operation_fences),(SELECT count(*) FROM claimcore.sessions),(SELECT count(*) FROM claimcore.sessions WHERE "
                         + SessionRows.expiredPredicate
                         + ") FROM claimcore.ledger_head")
                        [| box instant |]
                        token

                match rows with
                | [ row ] ->
                    return
                        {
                            InstallationId = installation
                            Baseline = Baseline.identifier
                            BaselineSha256 = Convert.ToHexStringLower Baseline.digest
                            BusinessTimeZone = zone
                            ObservedAt = instant
                            Position =
                                StoredEntries.position
                                    (DatabaseSession.value 0 row)
                                    (DatabaseSession.value 1 row)
                            Maintenance = mode
                            Formats =
                                {
                                    CaseRequest = RecordVersions.CaseRequest
                                    Snapshot = RecordVersions.Snapshot
                                    Authority = RecordVersions.Authority
                                    Control = RecordVersions.Control
                                    LedgerEntry = RecordVersions.LedgerEntry
                                }
                            Actors = DatabaseSession.value 2 row
                            Cases = DatabaseSession.value 3 row
                            Fences = DatabaseSession.value 4 row
                            Sessions = DatabaseSession.value 5 row
                            ExpiredSessions = DatabaseSession.value 6 row
                        }
                | _ -> return raise (RecordCorruptException "Ledger head is absent.")
            })
