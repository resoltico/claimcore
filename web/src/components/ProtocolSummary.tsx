import { CharacterWarning } from "./BusinessValue";
import { usePresentation } from "../presentation/context";
import { resolveSchema, selectedSchema, unionSelector } from "../protocol/forms";
import type { SchemaDocument, SchemaNode } from "../protocol/schema";
const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
type Props = { value: unknown; document: SchemaDocument; node: SchemaNode };
const summarySchema = ({ value, document, node }: Props): SchemaNode => {
  const schema = resolveSchema(document, node);
  if (!isRecord(value) || schema.oneOf === undefined) {
    return schema;
  }
  const nonNull = schema.oneOf.filter((branch) => resolveSchema(document, branch).type !== "null");
  if (nonNull.length === 1) {
    return resolveSchema(document, nonNull[0]!);
  }
  const selector = unionSelector(document, schema);
  const token = value[selector];
  return typeof token === "string"
    ? selectedSchema(document, schema, "", { [selector]: token })
    : schema;
};
const primitiveText = (value: unknown, p: ReturnType<typeof usePresentation>): string => {
  if (typeof value === "boolean") {
    return p.text(value ? "ui.yes" : "ui.no");
  }
  if (typeof value === "string" || typeof value === "number") {
    return String(value);
  }
  return p.text("ui.notRecorded");
};
const PrimitiveSummary = ({ value, document, node }: Props) => {
  const p = usePresentation();
  const schema = resolveSchema(document, node);
  if (value === null) {
    return <span>{p.text("ui.notRecorded")}</span>;
  }
  if (value === "") {
    return <span>{p.text("ui.emptyInput")}</span>;
  }
  const tokens = schema.enum ?? (Object.hasOwn(schema, "const") ? [schema.const] : []);
  if (typeof value === "string" && tokens.includes(value)) {
    return (
      <>
        <span>{p.token(value)}</span>{" "}
        <code>
          <bdi>{value}</bdi>
        </code>
      </>
    );
  }
  return (
    <span>
      <bdi style={{ whiteSpace: "pre-wrap" }}>{primitiveText(value, p)}</bdi>
      {typeof value === "string" ? <CharacterWarning value={value} /> : null}
    </span>
  );
};
/** Wire schema supplies token meaning; authored strings keep their exact spelling and whitespace. */
export const ProtocolSummary = (props: Props) => {
  const p = usePresentation();
  const { value, document } = props;
  if (Array.isArray(value)) {
    const schema = resolveSchema(document, props.node);
    return (
      <ul>
        {value.map((child: unknown) => (
          <li key={JSON.stringify(child)}>
            <ProtocolSummary value={child} document={document} node={schema.items ?? {}} />
          </li>
        ))}
      </ul>
    );
  }
  if (!isRecord(value)) {
    return <PrimitiveSummary {...props} />;
  }
  const schema = summarySchema(props);
  return (
    <dl>
      {Object.entries(value).map(([name, child]) => (
        <div key={name}>
          <dt>{p.fieldLabel(name)}</dt>
          <dd>
            <ProtocolSummary
              value={child}
              document={document}
              node={schema.properties?.[name] ?? {}}
            />
          </dd>
        </div>
      ))}
    </dl>
  );
};
