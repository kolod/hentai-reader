// Well-known repository paths, resolved from this file's location.
import { fileURLToPath } from "node:url";
import { join, resolve } from "node:path";

const root = resolve(fileURLToPath(new URL("../../..", import.meta.url)));
const contract = join(root, "contract");

export const paths = {
  root,
  contract,
  contractTypes: join(contract, "host-api.d.ts"),
  contractPackage: join(contract, "package.json"),
  prelude: join(contract, "prelude.js"),
  schema: join(contract, "schema", "contract.schema.json"),
  scriptsTsconfig: join(contract, "tsconfig.scripts.json"),
  contractTests: join(contract, "tests"),
};
