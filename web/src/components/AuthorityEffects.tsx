import type { AuthorityReviewEffect } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import { IdentifierValue } from "./IdentifierValue";

const EffectValues = ({ effect }: { effect: AuthorityReviewEffect }) => {
  const p = usePresentation();
  switch (effect.kind) {
    case "REGISTER":
    case "PROVISION_OWNER":
      return (
        <dl>
          <dt>{p.fieldLabel("principal.kind")}</dt>
          <dd>{p.term("principalKind", effect.principal.kind)}</dd>
          <dt>{p.fieldLabel("principal.issuer")}</dt>
          <dd>
            <bdi>{effect.principal.issuer}</bdi>
          </dd>
          <dt>{p.fieldLabel("principal.subject")}</dt>
          <dd>
            <bdi>{effect.principal.subject}</bdi>
          </dd>
        </dl>
      );
    case "SET_PROFILE":
      return (
        <dl>
          <dt>{p.fieldLabel("profile.displayName")}</dt>
          <dd>
            <bdi>{effect.profile.displayName}</bdi>
          </dd>
          {effect.profile.contact === null ? null : (
            <>
              <dt>{p.fieldLabel("profile.contact")}</dt>
              <dd>
                <bdi>{effect.profile.contact}</bdi>
              </dd>
            </>
          )}
        </dl>
      );
    case "GRANT":
    case "REVOKE":
      return (
        <dl>
          <dt>{p.fieldLabel("role")}</dt>
          <dd>{p.term("role", effect.grant.role)}</dd>
          <dt>{p.text("input.scope.label")}</dt>
          <dd>
            {p.term("scope", effect.grant.scope.kind)}{" "}
            {effect.grant.scope.kind === "CASE" ? (
              <IdentifierValue value={effect.grant.scope.caseReference} />
            ) : null}
          </dd>
        </dl>
      );
    case "ERASE_PROFILE":
    case "ENABLE":
    case "DISABLE":
      return null;
  }
};

/** The complete ordered effect list is supplied by the reviewed core plan. */
export const AuthorityEffects = ({
  effects,
}: {
  effects: ReadonlyArray<AuthorityReviewEffect>;
}) => {
  const p = usePresentation();
  return (
    <ol className="authority-effects">
      {effects.map((effect) => (
        <li key={JSON.stringify(effect)}>
          <h2>{p.text(`act.${effect.kind}.label`)}</h2>
          <EffectValues effect={effect} />
        </li>
      ))}
    </ol>
  );
};
