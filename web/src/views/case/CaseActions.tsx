import type { CurrentCase } from "../../api/service";
import { isCaseAct, type ActKind } from "../../operation/metadata";
import { RegionView } from "../../ui/regions/RegionView";
import { Actions } from "../../ui/regions/Actions";

const recommendation = (current: CurrentCase) =>
  current.screen.regions.find((region) => region.role === "RECOMMENDATION");
export const CaseActions = ({
  current,
  onAct,
}: {
  current: CurrentCase;
  onAct: (act: ActKind) => void;
}) => {
  const region = recommendation(current);
  if (region === undefined) {
    return null;
  }
  return (
    <RegionView
      region={region}
      onAct={(act) => {
        if (isCaseAct(act) && current.context.tasks.includes(`CASE:${act}`)) {
          onAct(act);
        }
      }}
    />
  );
};
export const CaseActionAlternatives = ({
  current,
  onAct,
}: {
  current: CurrentCase;
  onAct: (act: ActKind) => void;
}) => {
  const content = recommendation(current)?.content;
  if (content?.kind !== "ACTIONS") {
    return null;
  }
  return (
    <Actions
      content={{ ...content, recommended: null }}
      expandOther
      onAct={(act) => {
        if (isCaseAct(act) && current.context.tasks.includes(`CASE:${act}`)) {
          onAct(act);
        }
      }}
    />
  );
};
