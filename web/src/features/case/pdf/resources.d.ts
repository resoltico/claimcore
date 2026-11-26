declare module "virtual:attachment-preview-resources" {
  const paths: Readonly<Record<"cmaps" | "wasm", Readonly<Record<string, string>>>>;
  export default paths;
}

declare module "virtual:attachment-preview-worker" {
  const source: string;
  export default source;
}
