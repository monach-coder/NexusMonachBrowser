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

const multilingualToEnglishPath = path.resolve(process.argv[2]);
const englishToRussianPath = path.resolve(process.argv[3]);
const koreanToEnglishPath = path.resolve(process.argv[4]);
let multilingualToEnglish;
let englishToRussian;
let koreanToEnglish;

async function getMultilingualToEnglish() {
  multilingualToEnglish ??= await pipeline('translation', multilingualToEnglishPath, {
    dtype: 'q8',
    local_files_only: true,
  });
  return multilingualToEnglish;
}

async function getEnglishToRussian() {
  englishToRussian ??= await pipeline('translation', englishToRussianPath, {
    dtype: 'q8',
    local_files_only: true,
  });
  return englishToRussian;
}

async function getKoreanToEnglish() {
  koreanToEnglish ??= await pipeline('translation', koreanToEnglishPath, {
    dtype: 'q8',
    local_files_only: true,
  });
  return koreanToEnglish;
}

function translatedText(value) {
  let current = value;
  while (Array.isArray(current)) current = current[0];
  return typeof current?.translation_text === 'string' ? current.translation_text.trim() : '';
}

async function translateOne(item) {
  const original = typeof item?.text === 'string' ? item.text.trim() : '';
  if (!original) return { id: String(item?.id ?? ''), text: '' };

  let english = original;
  if (item?.source !== 'en') {
    const firstTranslator = item?.source === 'ko'
      ? await getKoreanToEnglish()
      : await getMultilingualToEnglish();
    const firstStage = await firstTranslator(original, {
      max_new_tokens: 384,
      num_beams: 1,
    });
    english = translatedText(firstStage);
    if (!english) throw new Error('The multilingual translation stage returned no text.');
  }

  const secondStage = await (await getEnglishToRussian())(english, {
    max_new_tokens: 384,
    num_beams: 1,
  });
  const russian = translatedText(secondStage);
  if (!russian) throw new Error('The Russian translation stage returned no text.');
  return { id: String(item?.id ?? ''), text: russian };
}

function translatedAt(value, index) {
  if (!Array.isArray(value)) return index === 0 ? translatedText(value) : '';
  return translatedText(value[index]);
}

async function translateBatch(items) {
  if (!items.length) return [];
  if (new Set(items.map(item => item?.source ?? 'auto')).size > 1)
    throw new Error('Mixed source routes use the safe per-item path.');
  const originals = items.map(item => String(item?.text ?? '').trim());
  const allEnglish = items.every(item => item?.source === 'en');
  const allKorean = items.every(item => item?.source === 'ko');
  let english = originals;
  if (!allEnglish) {
    const firstTranslator = allKorean ? await getKoreanToEnglish() : await getMultilingualToEnglish();
    const firstStage = await firstTranslator(originals, {
      max_new_tokens: 384,
      num_beams: 1,
    });
    english = originals.map((_, index) => translatedAt(firstStage, index));
    if (english.some(text => !text)) throw new Error('The multilingual translation batch returned incomplete text.');
  }
  const secondStage = await (await getEnglishToRussian())(english, {
    max_new_tokens: 384,
    num_beams: 1,
  });
  const russian = originals.map((_, index) => translatedAt(secondStage, index));
  if (russian.some(text => !text)) throw new Error('The Russian translation batch returned incomplete text.');
  return items.map((item, index) => ({ id: String(item?.id ?? ''), text: russian[index] }));
}

const input = readline.createInterface({ input: process.stdin, terminal: false });
for await (const line of input) {
  let requestId = '';
  try {
    const request = JSON.parse(line);
    requestId = String(request?.id ?? '');
    const items = Array.isArray(request?.items) ? request.items.slice(0, 16) : [];
    let translated;
    try {
      // Marian supports a small batch and ONNX executes it much faster than a
      // page full of individual calls. Twelve short DOM fragments keep RAM low.
      translated = await translateBatch(items);
    } catch {
      // A single malformed fragment must not discard the rest of the page.
      translated = [];
      for (const item of items) {
        try { translated.push(await translateOne(item)); }
        catch (error) {
          translated.push({ id: String(item?.id ?? ''), text: '',
            error: error instanceof Error ? error.message : String(error) });
        }
      }
    }
    process.stdout.write(JSON.stringify({ id: requestId, items: translated, error: '' }) + '\n');
  } catch (error) {
    process.stdout.write(JSON.stringify({
      id: requestId,
      items: [],
      error: error instanceof Error ? error.message : String(error),
    }) + '\n');
  }
}
