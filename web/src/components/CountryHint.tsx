import { usePresentation } from "../presentation/context";

export const CountryHint = ({ code }: { code: string }) => {
  const p = usePresentation();
  if (!/^[A-Z]{2}$/u.test(code)) {
    return null;
  }
  const name =
    code === "ZZ"
      ? p.text("ui.countryUnknown")
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
