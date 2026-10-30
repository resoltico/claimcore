namespace ClaimCore.Domain

open System.Globalization
open System.Numerics

/// Exact nonnegative totals use four decimal places internally, beyond System.Decimal's range.
[<Struct>]
type DecimalTotal = private DecimalTotal of BigInteger

module DecimalTotal =
    [<Literal>]
    let MaximumIntegerDigits = 40

    [<Literal>]
    let MaximumFractionalDigits = 4

    let Grammar =
        "(0|[1-9][0-9]{0,"
        + string (MaximumIntegerDigits - 1)
        + "})(\\.[0-9]{1,"
        + string MaximumFractionalDigits
        + "})?"

    let private scale = BigInteger.Pow(10I, MaximumFractionalDigits)

    let private maximum =
        BigInteger.Pow(10I, MaximumIntegerDigits + MaximumFractionalDigits) - 1I

    let private violation =
        Violation.DecimalFormat(MaximumIntegerDigits, MaximumFractionalDigits)

    let zero = DecimalTotal 0I

    let format (DecimalTotal units) =
        let integer = units / scale
        let fractional = units % scale
        let whole = integer.ToString(CultureInfo.InvariantCulture)

        if fractional.IsZero then
            whole
        else
            whole
            + "."
            + fractional
                .ToString("D" + string MaximumFractionalDigits, CultureInfo.InvariantCulture)
                .TrimEnd('0')

    let parse raw =
        ScalarText.parse None 0 raw
        |> Result.bind (fun value ->
            if not (ScalarText.matches Grammar value) then
                Error violation
            else
                let parts = value.Split '.'
                let integer = BigInteger.Parse(parts[0], CultureInfo.InvariantCulture)

                let fraction =
                    if parts.Length = 1 then
                        0I
                    else
                        BigInteger.Parse(
                            parts[1].PadRight(MaximumFractionalDigits, '0'),
                            CultureInfo.InvariantCulture
                        )

                Ok(DecimalTotal(integer * scale + fraction)))

    let sum values =
        let (DecimalTotal initial) = zero

        let units =
            values |> Seq.fold (fun total (DecimalTotal value) -> total + value) initial

        if units < 0I || units > maximum then
            Error violation
        else
            Ok(DecimalTotal units)
