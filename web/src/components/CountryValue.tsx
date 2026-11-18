import { usePresentation } from "../presentation/context";

/** Region names are presentation; the admitted code stays visible and unchanged. */
export const CountryValue = ({ value }: { value: string }) => {
  const p = usePresentation();
  const name = new Intl.DisplayNames(p.language === "en-XA" ? "en" : p.language, {
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
