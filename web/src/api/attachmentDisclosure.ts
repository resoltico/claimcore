import type { HumanDocument } from "@contracts/client";

const attachmentFields = (document: HumanDocument) => {
  for (const region of document.screen.regions) {
    if (region.content.kind === "ENTRIES") {
      const { entries } = region.content;
      const caseId = entries.find((entry) => entry.key === "attachmentCaseId")?.value;
      if (caseId?.kind === "IDENTIFIER") {
        const sequence = entries.find((entry) => entry.key === "attachmentDisclosure")?.value;
        return {
          caseId: caseId.value,
          sequence: sequence?.kind === "COUNT" ? sequence.value : undefined,
        };
      }
    }
  }
  return null;
};

export const attachmentDisclosureOf = (document: HumanDocument) => {
  const fields = attachmentFields(document);
  return fields?.sequence === undefined
    ? null
    : { caseId: fields.caseId, sequence: fields.sequence };
};

export const attachmentReceiptFields = (document: HumanDocument) =>
  document.subject.kind === "RECEIPT" ? attachmentFields(document) : null;
