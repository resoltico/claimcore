import * as components from "react-aria-components";

const required = [
  "ModalOverlay",
  "Modal",
  "Dialog",
  "Form",
  "TextField",
  "Button",
  "Checkbox",
  "FileTrigger",
];

if (!required.every((name) => components[name] !== undefined)) {
  throw new Error("React Aria Components does not expose the required accessible controls.");
}
