namespace ClaimCore.Domain

open System.Globalization
open System.Numerics

/// Exact totals use four decimal places internally, beyond System.Decimal's range.
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
        let absolute = BigInteger.Abs units
        let integer = absolute / scale
        let fractional = absolute % scale
        let sign = if units.Sign < 0 then "-" else ""
        let whole = sign + integer.ToString(CultureInfo.InvariantCulture)

        if fractional.IsZero then
            whole
        else
            whole
            + "."
            + fractional
                .ToString("D" + string MaximumFractionalDigits, CultureInfo.InvariantCulture)
                .TrimEnd('0')

    /// Precision comes from exact retained units, independently of localized or canonical rendering.
    let displayScale currency (DecimalTotal units) =
        let rec meaningful digits (fraction: BigInteger) =
            if fraction.IsZero then
                0
            elif fraction % 10I = 0I then
                meaningful (digits - 1) (fraction / 10I)
            else
                digits

        max
            (Money.minimumDisplayScale currency)
            (meaningful MaximumFractionalDigits (BigInteger.Abs units % scale))

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

    let signedGrammar = "-?" + Grammar

    let negate (DecimalTotal value) = DecimalTotal(-value)

    let parseSigned (raw: string) =
        if raw.StartsWith("-", System.StringComparison.Ordinal) then
            parse (raw.Substring 1) |> Result.map negate
        else
            parse raw

    let ofAmount amount =
        Amount.format amount
        |> parse
        |> Result.defaultWith (fun _ ->
            invalidOp "An admitted amount must fit an exact decimal total.")

    let sum values =
        let (DecimalTotal initial) = zero

        let units =
            values |> Seq.fold (fun total (DecimalTotal value) -> total + value) initial

        if BigInteger.Abs units > maximum then
            Error violation
        else
            Ok(DecimalTotal units)
