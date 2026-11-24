import { ChipList, chipValues } from "../ui/ChipList";
import { Chip, chipTone } from "../ui/Chip";
import type { PresentedValue as Value } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import { Fragment, type ReactNode } from "react";
import { moneyParts } from "../presentation/format";
import { ActorIdentity } from "./ActorIdentity";
import { CharacterWarning } from "./CharacterWarning";
import { CountryValue } from "./CountryValue";
import { IdentifierValue } from "./IdentifierValue";
import { breakAfter } from "./ValueBreaks";

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
      return <CountryValue value={value.value} />;
    case "PARTY":
    case "TEXT":
    case "NARRATIVE":
      return (
        <>
          <bdi dir="auto" className="plain-text-value">
            {breakAfter(value.value, /[-‐–—/._:@]+/gu)}
          </bdi>
          <CharacterWarning value={value.value} allowLineFeed={value.kind === "NARRATIVE"} />
        </>
      );
  }
};

const MoneyAmount = ({ value }: { value: Extract<Value, { kind: "MONEY" | "MONEY_TOTAL" }> }) => {
  const p = usePresentation();
  let offset = 0;
  const content: ReactNode[] = [];
  const parts = moneyParts(value.amount, value.currency, value.displayScale, p.displayLocale);
  for (const part of parts) {
    offset += part.value.length;
    content.push(
      part.type === "group" ? (
        <Fragment key={offset}>
          {part.value}
          <wbr />
        </Fragment>
      ) : (
        part.value
      ),
    );
  }
  return (
    <bdi dir="ltr" className="money-value">
      {content}
    </bdi>
  );
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
      return <MoneyAmount value={value} />;
    case "DATE_WITH_CODE":
      return (
        <span className="date-with-code">
          <time dateTime={value.value}>{p.date(value.value)}</time>
          <Chip value={value.code} />
        </span>
      );
    case "ACTOR":
      return <ActorIdentity actor={value.value} avatar={avatar} />;
    case "STATE":
      return <ChipList values={[value]} />;
    case "CODE":
      return chipTone(value) === undefined ? (
        <span>{p.term(value.vocabulary, value.token)}</span>
      ) : (
        <Chip value={value} />
      );
    case "CODES":
      return <ChipList values={chipValues(value)} />;
    case "ABSENT":
      return <span>{renderCatalogKey(p, value.reasonId)}</span>;
    case "MESSAGE":
      return <span>{renderCatalogKey(p, value.messageId)}</span>;
    default:
      return <ScalarValue value={value} timeZone={timeZone} />;
  }
};
