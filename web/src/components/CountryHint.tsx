import type { FieldCatalogEntry } from "../api/service";
import { usePresentation } from "../presentation/context";

export const CountryHint = ({ code, field }: { code: string; field: FieldCatalogEntry }) => {
  const p = usePresentation();
  const format =
    field.valueKind === "COUNTRY" && field.scalar.kind === "TEXT" ? field.scalar.format : undefined;
  if (
    format?.grammar === undefined ||
    format.grammar === null ||
    !new RegExp(`^(?:${format.grammar})$`, "u").test(code)
  ) {
    return null;
  }
  const name =
    code === format.unknownCode
      ? p.text("scalar.country.unknown")
      : new Intl.DisplayNames(p.language === "en-XA" ? "en" : p.language, {
          type: "region",
          fallback: "none",
        }).of(code);
  return (
    <small aria-live="polite">
      {name === undefined
        ? p.text("ui.countryUnassigned", { code })
        : p.text("ui.countryName", { code, name })}
    </small>
  );
};
