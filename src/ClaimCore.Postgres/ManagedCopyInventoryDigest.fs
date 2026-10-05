namespace ClaimCore.Postgres

open System
open System.Threading
open System.Globalization
open System.Security.Cryptography
open System.Text
open Npgsql

/// Bounded, ordered digest of the complete current managed-copy projection.
module internal ManagedCopyInventoryDigest =
    let compute (connection: NpgsqlConnection) transaction (ct: CancellationToken) =
        task {
            use command =
                new NpgsqlCommand(
                    "SELECT copy_id,revision,state,event_hash,ciphertext_sha256,"
                    + "ciphertext_bytes,retain_until FROM claimcore.managed_copies "
                    + "ORDER BY copy_id",
                    connection,
                    transaction
                )

            use! reader = command.ExecuteReaderAsync(ct)
            use digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            let mutable count = 0L

            while! reader.ReadAsync(ct) do
                count <- count + 1L

                if count > 1000000L then
                    invalidOp "Managed-copy inventory exceeds the reviewed bound."

                let entry =
                    String.Join(
                        "|",
                        [|
                            reader.GetGuid(0).ToString("D")
                            reader.GetInt64(1).ToString(CultureInfo.InvariantCulture)
                            reader.GetString(2)
                            reader.GetFieldValue<byte array>(3) |> Convert.ToHexStringLower
                            reader.GetFieldValue<byte array>(4) |> Convert.ToHexStringLower
                            reader.GetInt64(5).ToString(CultureInfo.InvariantCulture)
                            reader
                                .GetFieldValue<DateTimeOffset>(6)
                                .UtcTicks.ToString(CultureInfo.InvariantCulture)
                        |]
                    )
                    + "\n"

                digest.AppendData(Encoding.ASCII.GetBytes(entry))

            return count, digest.GetHashAndReset() |> Convert.ToHexStringLower
        }
