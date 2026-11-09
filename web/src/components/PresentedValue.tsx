import { Codes } from "../ui/regions/Codes";
import type { PresentedValue as Value } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import { moneyWithScale } from "../presentation/format";
import { productMetadata } from "../protocol/productMetadata";
import { ActorIdentity } from "./ActorIdentity";
import { CharacterWarning } from "./BusinessValue";
import { StateChip } from "./StateChip";
import { CountryValue } from "./CountryValue";
import { IdentifierValue } from "./IdentifierValue";

const country = productMetadata.catalog.fields.find((field) => field.valueKind === "COUNTRY")!;
type Scalar = Extract<
  Value,
  {
    kind:
      | "DATE"
      | "INSTANT"
      | "PARTY"
      | "TEXT"
      | "NARRATIVE"
      | "REFERENCE"
      | "IDENTIFIER"
      | "COUNT"
      | "COUNTRY"
      | "CURRENCY";
  }
>;
const ScalarValue = ({ value, timeZone }: { value: Scalar; timeZone: string }) => {
  const p = usePresentation();
  switch (value.kind) {
    case "DATE":
      return <time dateTime={value.value}>{p.date(value.value)}</time>;
    case "INSTANT":
      return (
        <time dateTime={value.value} title={value.value}>
          {p.recordedTime(value.value, timeZone)}
        </time>
      );
    case "COUNT":
      return <bdi>{p.integer(value.value)}</bdi>;
    case "REFERENCE":
    case "IDENTIFIER":
    case "CURRENCY":
      return <IdentifierValue value={value.value} />;
    case "COUNTRY":
      return <CountryValue value={value.value} field={country} />;
    case "PARTY":
    case "TEXT":
    case "NARRATIVE":
      return (
        <>
          <bdi dir="auto" className="plain-text-value">
            {value.value}
          </bdi>
          <CharacterWarning value={value.value} allowLineFeed={value.kind === "NARRATIVE"} />
        </>
      );
  }
};

/** Exhaustive presentation of supplied tagged values; no comparison, inference or name lookup. */
export const PresentedValue = ({
  value,
  timeZone,
  avatar = false,
}: {
  value: Value;
  timeZone: string;
  avatar?: boolean;
}) => {
  const p = usePresentation();
  switch (value.kind) {
    case "MONEY":
    case "MONEY_TOTAL":
      return (
        <bdi dir="ltr">
          {moneyWithScale(value.amount, value.currency, value.displayScale, p.displayLocale)}
        </bdi>
      );
    case "ACTOR":
      return <ActorIdentity actor={value.value} avatar={avatar} />;
    case "STATE":
      return <StateChip value={value.token} />;
    case "CODE":
      return (
        <span className="code-value" data-vocabulary={value.vocabulary} data-code={value.token}>
          {p.term(value.vocabulary, value.token)}
        </span>
      );
    case "CODES":
      return <Codes content={value} />;
    case "ABSENT":
      return <span>{renderCatalogKey(p, value.reasonId)}</span>;
    case "MESSAGE":
      return <span>{renderCatalogKey(p, value.messageId)}</span>;
    default:
      return <ScalarValue value={value} timeZone={timeZone} />;
  }
};
