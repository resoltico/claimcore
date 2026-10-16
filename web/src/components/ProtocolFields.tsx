import type { SchemaDocument, SchemaNode } from "../protocol/schema";
import { resolveSchema, selectedSchema, unionSelector, type FormValues } from "../protocol/forms";
import { usePresentation } from "../presentation/context";

type Props = {
  document: SchemaDocument;
  node: SchemaNode;
  path: string;
  values: FormValues;
  disabled: boolean;
  errorPath?: string | null;
  onChange: (path: string, value: string | boolean) => void;
};
const text = (value: unknown): string => (typeof value === "string" ? value : "");
const Choice = ({
  path,
  values,
  disabled,
  onChange,
  options,
  errorPath,
}: Omit<Props, "document" | "node"> & { options: ReadonlyArray<unknown> }) => {
  const p = usePresentation();
  return (
    <label>
      {p.fieldLabel(path.split(".").at(-1)!)}
      <select
        name={path}
        value={text(values[path] ?? options[0])}
        disabled={disabled}
        aria-invalid={errorPath === path || undefined}
        onChange={(event) => {
          onChange(path, event.target.value);
        }}
      >
        {options.map((value) => (
          <option key={text(value)} value={text(value)}>
            {p.token(text(value))}
          </option>
        ))}
      </select>
    </label>
  );
};
const ScalarInput = ({ path, values, disabled, onChange, node, errorPath }: Props) => {
  const p = usePresentation();
  const label = p.fieldLabel(path.split(".").at(-1)!);
  if (node.type === "boolean") {
    return (
      <label>
        <input
          name={path}
          type="checkbox"
          checked={values[path] === true}
          disabled={disabled}
          aria-invalid={errorPath === path || undefined}
          onChange={(event) => {
            onChange(path, event.target.checked);
          }}
        />
        {label}
      </label>
    );
  }
  return (
    <label>
      {label}
      <input
        name={path}
        type={node.format === "date" ? "date" : "text"}
        value={text(values[path])}
        disabled={disabled}
        aria-invalid={errorPath === path || undefined}
        autoComplete="off"
        dir="auto"
        onChange={(event) => {
          onChange(path, event.target.value);
        }}
      />
    </label>
  );
};
const UnionInput = (props: Props) => {
  const p = usePresentation();
  const { document, node, path, values } = props;
  const selector = unionSelector(document, node);
  const options = node.oneOf!.map(
    (branch) => resolveSchema(document, branch).properties?.[selector]?.const,
  );
  return (
    <fieldset>
      <legend>{p.fieldLabel(path.split(".").at(-1)!)}</legend>
      <Choice {...props} path={`${path}.${selector}`} options={options} />
      <ProtocolFields {...props} node={selectedSchema(document, node, path, values)} />
    </fieldset>
  );
};
export function ProtocolFields(props: Props) {
  const { document, node, path } = props;
  const schema = resolveSchema(document, node);
  if (schema.oneOf !== undefined) {
    return <UnionInput {...props} node={schema} />;
  }
  if (Object.hasOwn(schema, "const")) {
    return null;
  }
  if (schema.type === "object") {
    return (
      <>
        {Object.entries(schema.properties ?? {}).map(([name, child]) => (
          <ProtocolFields
            key={name}
            {...props}
            node={child}
            path={path ? `${path}.${name}` : name}
          />
        ))}
      </>
    );
  }
  if (schema.enum !== undefined) {
    return <Choice {...props} options={schema.enum} />;
  }
  return <ScalarInput {...props} node={schema} />;
}
