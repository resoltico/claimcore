import { copyFile, readFile, readdir, rm, writeFile } from "node:fs/promises";
import { join } from "node:path";

const heading =
  "// Generated from ClaimCore.Contracts. Do not edit.\nnamespace ClaimCore.Protocol\n\nopen System.Text.Json\n\n";
const maximumLines = 280;

// Pack whole formatted declarations/modules, never split a responsibility to satisfy a line limit.
const pack = (prefix, blocks) => {
  const files = [];
  let body = "";
  for (const block of blocks) {
    if ((heading + block).split("\n").length > maximumLines)
      throw new Error("One protocol declaration exceeds the reviewed generated-source budget.");
    if ((heading + body + block).split("\n").length > maximumLines) {
      files.push([`${prefix}${String(files.length).padStart(3, "0")}.fs`, heading + body]);
      body = "";
    }
    body += `${block.trimEnd()}\n\n`;
  }
  if (body !== "")
    files.push([`${prefix}${String(files.length).padStart(3, "0")}.fs`, heading + body]);
  return files;
};

const formatSources = async (directory, format) => {
  await format(directory);
  const names = (await readdir(directory)).sort();
  const families = ["Types", "Scalar", "Codec", "Binding"];
  const packed = new Map();
  for (const prefix of families) {
    const members = names.filter((name) => name.startsWith(prefix) && name.endsWith(".fs"));
    const blocks = await Promise.all(
      members.map(async (name) => {
        const text = await readFile(join(directory, name), "utf8");
        if (!text.startsWith(heading)) throw new Error("Protocol source heading is not canonical.");
        return text.slice(heading.length);
      }),
    );
    if (blocks.length === 0)
      throw new Error("Protocol generation omitted a required source family.");
    await Promise.all(members.map((name) => rm(join(directory, name))));
    const files = pack(prefix, blocks);
    await Promise.all(files.map(([name, content]) => writeFile(join(directory, name), content)));
    packed.set(
      prefix,
      files.map(([name]) => name),
    );
  }
  const ordered = [
    ...packed.get("Types"),
    "Definition.fs",
    ...packed.get("Scalar"),
    ...packed.get("Codec"),
    ...packed.get("Binding"),
    "WebV2.fs",
  ];
  const properties = [
    "<Project>",
    "  <ItemGroup>",
    ...ordered.map((name) => `    <Compile Include="$(MSBuildThisFileDirectory)${name}" />`),
    "  </ItemGroup>",
    "</Project>",
    "",
  ].join("\n");
  await writeFile(join(directory, "Protocol.Generated.props"), properties);
  await format(directory);
};

// Temporary output must obey the same reviewed formatting policy as checked-in product code.
export const formatProtocolSources = async (directory, format, configuration) => {
  const target = join(directory, ".editorconfig");
  await copyFile(configuration, target);
  try {
    await formatSources(directory, format);
  } finally {
    await rm(target);
  }
};
