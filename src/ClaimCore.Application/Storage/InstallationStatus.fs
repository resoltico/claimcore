namespace ClaimCore.Application.Storage

open ClaimCore.Application.Verification

open ClaimCore.Application.Reads

open ClaimCore.Application.Execution

open ClaimCore.Application

open System
open System.Threading
open Npgsql
open ClaimCore.Domain
open ClaimCore.Application.Contracts
open ClaimCore.RecordFormat

module internal InstallationStatus =
    let read (source: NpgsqlDataSource) token =
        DatabaseSession.read source token (fun connection ->
            task {
                ServerSupport.require connection.PostgreSqlVersion
                let! installation, zone = Baseline.require connection token
                let! _ = Transactions.configure connection Transactions.timeouts token
                let! instant = SessionRows.now connection token
                let! mode = RegisterMaintenanceStore.read connection token

                let! owner =
                    DatabaseSession.rows
                        connection
                        "SELECT a.actor_id,a.principal_kind,a.issuer,a.subject,a.enabled,a.display_name,a.contact FROM claimcore.actors a JOIN claimcore.ledger_effects e ON e.subject_id=a.actor_id WHERE e.subject_kind='PEOPLE' AND e.act='people.provisionOwner' ORDER BY e.operation_sequence,e.effect_index LIMIT 1"
                        [||]
                        token

                let owner =
                    owner
                    |> List.tryExactlyOne
                    |> Option.map (Projections.actorRow >> Actor.currentReference)


                let! rows =
                    DatabaseSession.rows
                        connection
                        ("SELECT sequence,entry_hash,(SELECT count(*) FROM claimcore.actors),(SELECT count(*) FROM claimcore.cases),(SELECT count(*) FROM claimcore.sessions),(SELECT count(*) FROM claimcore.sessions WHERE "
                         + SessionRows.expiredPredicate
                         + "),(SELECT count(*) FROM claimcore.case_work),(SELECT count(*) FROM claimcore.reminders) FROM claimcore.ledger_head")
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
                            InstallationFormat = InstallationFormat.Current
                            InitialOwner = owner
                            Actors = DatabaseSession.value 2 row
                            Cases = DatabaseSession.value 3 row
                            Sessions = DatabaseSession.value 4 row
                            ExpiredSessions = DatabaseSession.value 5 row
                            WorkRecords = DatabaseSession.value 6 row
                            Reminders = DatabaseSession.value 7 row
                        }
                | _ -> return raise (RecordCorruptException "Ledger head is absent.")
            })
