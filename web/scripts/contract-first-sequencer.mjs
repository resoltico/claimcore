import { BaseSequencer } from "vitest/node";

// Pure contracts reject broad mutations before slower component workflows.
export default class ContractFirstSequencer extends BaseSequencer {
  /** @override @param {import("vitest/node").TestSpecification[]} files */
  async sort(files) {
    const ordered = await super.sort(files);
    return ordered.sort(
      (left, right) =>
        Number(left.moduleId.endsWith(".test.tsx")) - Number(right.moduleId.endsWith(".test.tsx")),
    );
  }
}
