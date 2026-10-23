import { DescriptorField } from "./DescriptorField";
import { productMetadata } from "../protocol/productMetadata";
import type { Notice } from "../api/notices";
import { NoticeView } from "../presentation/Message";
import type { SchemaDocument, SchemaNode } from "../protocol/schema";
import {
  resolveSchema,
  selectedSchema,
  unionSelector,
  schemaConstant,
  type FormValues,
} from "../protocol/forms";
import { usePresentation } from "../presentation/context";

type Props = {
  document: SchemaDocument;
  node: SchemaNode;
  path: string;
  values: FormValues;
  disabled: boolean;
  errorPath?: string | null;
  errorNotice?: Notice | null;
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
  errorNotice,
}: Omit<Props, "document" | "node"> & { options: ReadonlyArray<unknown> }) => {
  const p = usePresentation();
  return (
    <>
      <label>
        {path.endsWith(".mode") ? p.text("ui.action") : p.fieldLabel(path.split(".").at(-1)!)}
        <select
          id={path.endsWith(".mode") ? `correction-${path.split(".").at(-2)}-mode` : undefined}
          name={path}
          value={text(values[path] ?? options[0])}
          disabled={disabled}
          aria-invalid={errorPath === path || undefined}
          aria-describedby={errorPath === path && errorNotice ? `${path}-error` : undefined}
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
      {errorPath === path && errorNotice ? (
        <small id={`${path}-error`}>
          <NoticeView value={errorNotice} />
        </small>
      ) : null}
    </>
  );
};
const BooleanInput = ({ path, values, disabled, onChange, errorPath }: Props) => {
  const p = usePresentation();
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
      {p.fieldLabel(path.split(".").at(-1)!)}
    </label>
  );
};
const ScalarInput = (props: Props) => {
  const { path, values, disabled, onChange, node, errorPath, errorNotice } = props;
  const p = usePresentation();
  const name = path.split(".").at(-1)!;
  const field = [
    ...productMetadata.descriptors.fields,
    ...productMetadata.descriptors.inputFields,
  ].find((candidate) => candidate.name === name);
  const label = p.fieldLabel(name);
  if (field !== undefined) {
    return (
      <DescriptorField
        field={field}
        inputName={path}
        value={text(values[path])}
        disabled={disabled}
        error={errorPath === path ? (errorNotice ?? undefined) : undefined}
        onChange={(value) => {
          onChange(path, value);
        }}
      />
    );
  }
  if (node.type === "boolean") {
    return <BooleanInput {...props} />;
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
        aria-describedby={errorPath === path && errorNotice ? `${path}-error` : undefined}
        autoComplete="off"
        dir="auto"
        onChange={(event) => {
          onChange(path, event.target.value);
        }}
      />
      {errorPath === path && errorNotice ? (
        <small id={`${path}-error`}>
          <NoticeView value={errorNotice} />
        </small>
      ) : null}
    </label>
  );
};
const UnionInput = (props: Props) => {
  const p = usePresentation();
  const { document, node, path, values } = props;
  const selector = unionSelector(document, node);
  const group = path.split(".").at(-1)!;
  const options = node.oneOf!.map((branch) =>
    schemaConstant(document, resolveSchema(document, branch).properties?.[selector] ?? {}),
  );
  return (
    <fieldset>
      <legend>{selector === "mode" ? p.groupLabel(group) : p.fieldLabel(group)}</legend>
      {selector === "mode" ? <p>{p.groupMeaning(group)}</p> : null}
      <Choice {...props} path={path ? `${path}.${selector}` : selector} options={options} />
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
