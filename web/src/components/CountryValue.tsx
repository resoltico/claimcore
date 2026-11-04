import type { FieldCatalogEntry } from "../api/service";
import { usePresentation } from "../presentation/context";

/** Region names are presentation; the admitted code stays visible and unchanged. */
export const CountryValue = ({ value, field }: { value: string; field: FieldCatalogEntry }) => {
  const p = usePresentation();
  const unknown = field.scalar.kind === "TEXT" ? field.scalar.format?.unknownCode : undefined;
  const name =
    value === unknown
      ? p.text("scalar.country.unknown")
      : new Intl.DisplayNames(p.language === "en-XA" ? "en" : p.language, {
          type: "region",
          fallback: "none",
        }).of(value);
  return (
    <span>
      {name === undefined ? null : <span>{name} </span>}
      <bdi dir="ltr">{value}</bdi>
    </span>
  );
};
