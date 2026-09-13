namespace ClaimCore.Postgres

open System
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open ClaimCore.Domain
open ClaimCore.Application
open ClaimCore.RecordFormat

/// Transactional relational adapter. Advisory locks serialise absent rows and operation-ID replays.
/// No business transition is implemented in SQL or here: the application supplies the domain decision.
type internal PostgresStore private (dataSource: NpgsqlDataSource, ownsDataSource: bool) =
    new(connectionString: string) =
        let dataSource = RuntimeDataSource.create connectionString
        new PostgresStore(dataSource, true)

    new(dataSource: NpgsqlDataSource) = new PostgresStore(dataSource, false)

    /// Startup diagnostic; every actual store operation repeats the same checks independently.
    member _.CheckSchema() =
        StoreData.read dataSource (fun _ -> Task.FromResult())

    interface IClaimStore with
        member _.Transact(operation, decide) =
            StoreTransaction.transact dataSource operation decide

        member _.Get(reference) =
            StoreData.read dataSource (fun connection ->
                task {
                    use command = new NpgsqlCommand(Sql.selectCase, connection)
                    Sql.text command "reference" reference
                    let! result = command.ExecuteReaderAsync()
                    use reader = result
                    let! exists = reader.ReadAsync()
                    return if exists then Some(Rows.claim reader) else None
                })

        member _.List(after) =
            StoreData.read dataSource (fun connection ->
                task {
                    use command = new NpgsqlCommand(Sql.listCases, connection)
                    Sql.optional command "after" NpgsqlDbType.Text after
                    let! result = command.ExecuteReaderAsync()
                    use reader = result
                    let results = ResizeArray<Claim>()
                    let mutable hasRow = true

                    while hasRow do
                        let! row = reader.ReadAsync()
                        hasRow <- row

                        if row then
                            results.Add(Rows.claim reader)

                    let pageSize = SemanticContract.current.MaximumPageSize
                    let items = results |> Seq.truncate pageSize |> Seq.toList

                    let page: CasePage =
                        {
                            Items = items
                            NextAfter =
                                if results.Count > pageSize then
                                    items
                                    |> List.tryLast
                                    |> Option.map (fun value ->
                                        (Claim.view value).Fields.CaseReference)
                                else
                                    None
                        }

                    return page
                })

        member _.History(reference, afterVersion) =
            StoreData.read dataSource (fun connection ->
                task {
                    use command = new NpgsqlCommand(Sql.history, connection)
                    Sql.text command "reference" reference
                    Sql.integer command "after" afterVersion
                    let! result = command.ExecuteReaderAsync()
                    use reader = result
                    let results = ResizeArray<Receipt>()
                    let mutable hasRow = true

                    while hasRow do
                        let! row = reader.ReadAsync()
                        hasRow <- row

                        if row then
                            results.Add(Rows.receipt reader false)

                    let pageSize = SemanticContract.current.MaximumPageSize
                    let items = results |> Seq.truncate pageSize |> Seq.toList

                    let page: ClaimCore.Application.HistoryPage =
                        {
                            Items = items
                            NextAfterVersion =
                                if results.Count > pageSize then
                                    items
                                    |> List.tryLast
                                    |> Option.map (fun item -> (Claim.view item.Case).Version)
                                else
                                    None
                        }

                    return page
                })

        member _.Operation(operationId) =
            StoreData.read dataSource (fun connection ->
                task {
                    use command = new NpgsqlCommand(Sql.operation, connection)
                    Sql.uuid command "operation" operationId
                    let! result = command.ExecuteReaderAsync()
                    use reader = result
                    let! exists = reader.ReadAsync()

                    return if exists then Some(Rows.receipt reader true) else None
                })

    interface IDisposable with
        member _.Dispose() =
            if ownsDataSource then
                dataSource.Dispose()
