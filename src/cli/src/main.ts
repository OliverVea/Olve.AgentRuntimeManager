#!/usr/bin/env bun
import { run } from "./cli";

// Set exitCode rather than calling process.exit() so buffered stdout is flushed when piped.
process.exitCode = await run(process.argv.slice(2), {
  stdout: (text) => process.stdout.write(`${text}\n`),
  stderr: (text) => process.stderr.write(`${text}\n`),
  env: process.env,
});
