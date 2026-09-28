module internal ClaimCore.IntegrationTests.RestorePhysicalConnections

open Npgsql
open ClaimCore.IntegrationTests.Fixtures

[<NoEquality; NoComparison>]
type RestoredPairAccess =
    {
        Owner: string
        App: string
        WitnessWriter: string
        WitnessAudit: string
        WitnessOwner: string
    }

let private portConnection (source: string) port =
    let value = NpgsqlConnectionStringBuilder(source)
    value.Host <- "127.0.0.1"
    value.Port <- port
    value.ConnectionString

let private witnessDatabase (source: string) (writer: string) port =
    let value = NpgsqlConnectionStringBuilder(source)
    let selected = NpgsqlConnectionStringBuilder(writer)
    value.Host <- "127.0.0.1"
    value.Port <- port
    value.Database <- selected.Database
    value.ConnectionString

let restoredAccess owner app writer primaryPort witnessPort =
    {
        Owner = portConnection owner primaryPort
        App = portConnection app primaryPort
        WitnessWriter = witnessDatabase writer writer witnessPort
        WitnessAudit = witnessDatabase (witnessAuditConnection ()) writer witnessPort
        WitnessOwner = witnessDatabase (witnessOwnerConnection ()) writer witnessPort
    }
