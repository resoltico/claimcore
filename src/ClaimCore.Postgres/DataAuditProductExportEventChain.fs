namespace ClaimCore.Postgres

open System
open System.Threading
open Npgsql
open DataAuditCommon

[<NoEquality; NoComparison>]
type private ProductCopyState =
    {
        CopyId: Guid
        Revision: int64
        EventHash: byte array
        State: string
    }

/// Checks each product export's immutable REGISTER/ADOPT prefix. Signed later
/// revisions are replayed from the verified adoption by DataAuditCopyAdoptions.
module internal DataAuditProductExportEventChain =
    let private page (connection: NpgsqlConnection) transaction after (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT copy_id,revision,event_hash,state FROM claimcore.managed_copies "
                    + "WHERE producer_kind='PRODUCT_EXPORT' AND copy_id>@after "
                    + "ORDER BY copy_id LIMIT 50",
                    connection,
                    transaction
                )

            Sql.uuid command "after" after
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<ProductCopyState>()

            while reader.Read() do
                rows.Add
                    {
                        CopyId = reader.GetGuid(0)
                        Revision = reader.GetInt64(1)
                        EventHash = reader.GetFieldValue<byte array>(2)
                        State = reader.GetString(3)
                    }

            return rows.ToArray()
        }

    let private events (connection: NpgsqlConnection) transaction copyId (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT revision,event_kind,event_hash,previous_hash "
                    + "FROM claimcore.managed_copy_events WHERE copy_id=@copy "
                    + "ORDER BY revision LIMIT 3",
                    connection,
                    transaction
                )

            Sql.uuid command "copy" copyId
            use! reader = command.ExecuteReaderAsync(ct)
            let rows = ResizeArray<int64 * string * byte array * byte array>()

            while reader.Read() do
                rows.Add(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetFieldValue<byte array>(2),
                    reader.GetFieldValue<byte array>(3)
                )

            return rows.ToArray()
        }

    let private closed (state: ProductCopyState) rows =
        match rows with
        | [| (1L, "REGISTER", first, previous) |] ->
            previous = Array.zeroCreate<byte> 32
            && state.Revision = 1L
            && state.EventHash = first
            && state.State = "UNKNOWN"
        | [| (1L, "REGISTER", first, previous); (2L, "ADOPT", second, prior) |] ->
            previous = Array.zeroCreate<byte> 32
            && prior = first
            && state.Revision = 2L
            && state.EventHash = second
            && state.State = "UNVERIFIED"
        | [| (1L, "REGISTER", first, previous)
             (2L, "ADOPT", second, prior)
             (3L, _, _, laterPrevious) |] ->
            previous = Array.zeroCreate<byte> 32
            && prior = first
            && laterPrevious = second
            && state.Revision >= 3L
        | _ -> false

    let verify connection transaction (ct: CancellationToken) =
        task {
            let mutable after = Guid.Empty
            let mutable more = true
            let mutable count = 0L

            while more do
                let! copies = page connection transaction after ct

                for copy in copies do
                    if copy.CopyId <= after then
                        corrupt ()

                    let! rows = events connection transaction copy.CopyId ct

                    if not (closed copy rows) then
                        corrupt ()

                    after <- copy.CopyId
                    count <- count + 1L

                more <- copies.Length = 50

            return count
        }
