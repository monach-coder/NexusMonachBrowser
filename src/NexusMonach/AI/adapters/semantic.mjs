import readline from 'node:readline';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const { pipeline, env } = require(path.join(here, '..', 'node_modules', '@huggingface', 'transformers'));
env.allowRemoteModels = false;
env.allowLocalModels = true;
env.useBrowserCache = false;
const modelPath = path.resolve(process.argv[2]);
const extractor = await pipeline('feature-extraction', modelPath, { dtype: 'fp32', local_files_only: true });

const input = readline.createInterface({ input: process.stdin, terminal: false });
for await (const line of input) {
  try {
    const request = JSON.parse(line);
    const text = typeof request.text === 'string' ? request.text : '';
    // E5 uses different prefixes for a query and a document. Callers that
    // already selected one must not be silently converted into a passage.
    const prepared = /^(query|passage):\s/i.test(text) ? text : 'passage: ' + text;
    const value = await extractor(prepared, { pooling: 'mean', normalize: true });
    process.stdout.write(JSON.stringify(Array.from(value.data)) + '\n');
  } catch {
    process.stdout.write('[]\n');
  }
}
