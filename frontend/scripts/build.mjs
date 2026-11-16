// Production build launcher: injects the footer version with `ng build --define`.
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { buildArgs, readPackageVersion } from './build-args.mjs';

let args;
try {
  args = buildArgs(process.env, readPackageVersion());
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exit(1);
}

const ng = createRequire(import.meta.url).resolve('@angular/cli/bin/ng.js');
const result = spawnSync(process.execPath, [ng, ...args], { stdio: 'inherit' });
if (result.error) {
  console.error(result.error.message);
  process.exit(1);
}
process.exit(result.status ?? 1);
