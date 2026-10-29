import { IdentifierValue } from "./IdentifierValue";
import { isTerm, isVocabulary } from "../presentation/terms";
import { CharacterWarning } from "./BusinessValue";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import { atPath, type CatalogEntry } from "../protocol/catalogAssembly";

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
const samePath = (left: ReadonlyArray<string>, right: ReadonlyArray<string>): boolean =>
  left.length === right.length && left.every((part, index) => part === right[index]);
const startsWithPath = (path: ReadonlyArray<string>, prefix: ReadonlyArray<string>): boolean =>
  prefix.every((part, index) => part === path[index]);
type Bindings = Pick<CatalogEntry, "inputs" | "constants">;
const activeBindings = (value: unknown, entry: CatalogEntry): Bindings => {
  const active = (binding: Pick<CatalogEntry["inputs"][number], "branches">): boolean =>
    binding.branches.every((branch) => atPath(value, branch.path) === branch.value);
  return { inputs: entry.inputs.filter(active), constants: entry.constants.filter(active) };
};
const known = (bindings: Bindings, path: ReadonlyArray<string>): boolean =>
  [...bindings.inputs, ...bindings.constants].some((binding) => startsWithPath(binding.path, path));
const label = (
  bindings: Bindings,
  path: ReadonlyArray<string>,
  p: ReturnType<typeof usePresentation>,
): string => {
  const input = bindings.inputs.find((candidate) => samePath(candidate.path, path));
  if (input !== undefined) {
    return input.kind.kind === "GROUP"
      ? p.text("label.action")
      : renderCatalogKey(p, input.labelId);
  }
  const group = bindings.inputs.find(
    (candidate) => candidate.kind.kind === "GROUP" && samePath(candidate.path.slice(0, -1), path),
  );
  if (group !== undefined) {
    return renderCatalogKey(p, group.labelId);
  }
  const name = path.at(-1)!;
  return name === "kind" ? p.text("label.action") : p.fieldLabel(name);
};
const isToken = (value: string, path: ReadonlyArray<string>, bindings: Bindings): boolean => {
  const input = bindings.inputs.find((candidate) => samePath(candidate.path, path));
  const constant = bindings.constants.find((candidate) => samePath(candidate.path, path));
  const choices =
    input?.kind.kind === "CHOICE" || input?.kind.kind === "GROUP" ? input.kind.options : [];
  return choices.includes(value) || constant?.value === value;
};
const bindingVocabulary = (bindings: Bindings, path: ReadonlyArray<string>): string | null => {
  const input = bindings.inputs.find((candidate) => samePath(candidate.path, path));
  if (input?.kind.kind === "CHOICE" || input?.kind.kind === "GROUP") {
    return input.kind.vocabulary;
  }
  return bindings.constants.find((candidate) => samePath(candidate.path, path))?.vocabulary ?? null;
};
type ValueProps = { value: unknown; path: ReadonlyArray<string>; bindings: Bindings };
const primitiveText = (value: unknown, p: ReturnType<typeof usePresentation>): string => {
  if (typeof value === "boolean") {
    return p.text(value ? "ui.yes" : "ui.no");
  }
  if (typeof value === "string" || typeof value === "number") {
    return String(value);
  }
  return JSON.stringify(value);
};
const isIdentifier = (path: ReadonlyArray<string>, bindings: Bindings): boolean => {
  const input = bindings.inputs.find((candidate) => samePath(candidate.path, path));
  return input?.source === "OPERATION_IDENTITY" || input?.field === "caseReference";
};
const PrimitiveSummary = ({ value, path, bindings }: ValueProps) => {
  const p = usePresentation();
  const vocabulary = bindingVocabulary(bindings, path);
  if (
    typeof value === "string" &&
    isToken(value, path, bindings) &&
    vocabulary !== null &&
    isVocabulary(vocabulary) &&
    isTerm(vocabulary, value)
  ) {
    return (
      <>
        <span>{p.term(vocabulary, value)}</span>{" "}
        <code>
          <bdi dir="ltr">{value}</bdi>
        </code>
      </>
    );
  }
  if (value === null) {
    return <span>{p.text("ui.notRecorded")}</span>;
  }
  if (value === "") {
    return <span>{p.text("ui.emptyInput")}</span>;
  }
  return (
    <span>
      {isIdentifier(path, bindings) ? (
        <IdentifierValue value={primitiveText(value, p)} />
      ) : (
        <bdi style={{ whiteSpace: "pre-wrap" }}>{primitiveText(value, p)}</bdi>
      )}
      {typeof value === "string" ? <CharacterWarning value={value} /> : null}
    </span>
  );
};
const rows = (
  value: Record<string, unknown>,
  path: ReadonlyArray<string>,
  bindings: Bindings,
): { path: string[]; value: unknown }[] =>
  Object.entries(value).flatMap(([name, nested]) => {
    const next = [...path, name];
    return ["values", "groups"].includes(name) && isRecord(nested) && known(bindings, next)
      ? rows(nested, next, bindings)
      : [{ path: next, value: nested }];
  });
const SummaryValue = ({ value, path, bindings }: ValueProps) => {
  const p = usePresentation();
  if (!known(bindings, path)) {
    return (
      <pre>
        <bdi dir="ltr">{JSON.stringify(value, null, 2)}</bdi>
      </pre>
    );
  }
  if (!isRecord(value)) {
    return <PrimitiveSummary value={value} path={path} bindings={bindings} />;
  }
  return (
    <dl>
      {rows(value, path, bindings).map((row) => (
        <div key={row.path.join(".")}>
          <dt>{label(bindings, row.path, p)}</dt>
          <dd>
            <SummaryValue {...row} bindings={bindings} />
          </dd>
        </div>
      ))}
    </dl>
  );
};
/** Catalog bindings supply meaning; unrecognized members remain visible as raw JSON. */
export const ProtocolSummary = ({ value, entry }: { value: unknown; entry: CatalogEntry }) => (
  <SummaryValue value={value} path={[]} bindings={activeBindings(value, entry)} />
);
