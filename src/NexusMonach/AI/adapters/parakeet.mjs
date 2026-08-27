// Parakeet TDT 0.6B v3 — распознавание речи с таймкодами через ONNX.
// Протокол: stdin (JSON per line) → stdout (JSON per line).
// Запуск: node parakeet.mjs <путь_к_моделям>
import readline from 'node:readline';
import path from 'node:path';
import fs from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const ort = require(path.join(here, '..', 'node_modules', 'onnxruntime-node'));

const modelDir = path.resolve(process.argv[2]);

// ─── Словарь SentencePiece ─────────────────────────────────────────────────
const vocabLines = fs.readFileSync(path.join(modelDir, 'vocab.txt'), 'utf8')
    .split('\n').filter(Boolean);
const idToToken = new Map();
const tokenToId = new Map();
for (const line of vocabLines) {
    const space = line.lastIndexOf(' ');
    const token = line.substring(0, space);
    const id = parseInt(line.substring(space + 1), 10);
    idToToken.set(id, token);
    tokenToId.set(token, id);
}
const BLANK_ID = tokenToId.get('<blk>') ?? idToToken.size - 1;

// ─── Конфигурация признаков ────────────────────────────────────────────────
const SAMPLE_RATE = 16000;
const SUBSAMPLING = 8;      // из config.json
const HOP_SIZE = 160;       // стандартный hop для 16 кГц
const FRAME_SECONDS = SUBSAMPLING * HOP_SIZE / SAMPLE_RATE; // 0.08 с

// ─── ONNX сессии ────────────────────────────────────────────────────────────
let featureSession, encoderSession, decoderSession;

async function initialize() {
    featureSession = await ort.InferenceSession.create(
        path.join(modelDir, 'nemo128.onnx'));
    encoderSession = await ort.InferenceSession.create(
        path.join(modelDir, 'encoder-model.int8.onnx'));
    decoderSession = await ort.InferenceSession.create(
        path.join(modelDir, 'decoder_joint-model.int8.onnx'));
}

// ─── Инференс ───────────────────────────────────────────────────────────────
async function transcribe(pcmSamples) {
    // Минимальная длина для mel-спектрограммы: одно окно + hop
    const MIN_SAMPLES = SAMPLE_RATE / 10; // 100 мс
    if (pcmSamples.length < MIN_SAMPLES) {
        return { text: '', segments: [] };
    }

    // 1. Фичи: PCM → mel-спектрограмма
    const waveforms = new ort.Tensor('float32', Float32Array.from(pcmSamples), [1, pcmSamples.length]);
    const waveforms_lens = new ort.Tensor('int64', BigInt64Array.from([BigInt(pcmSamples.length)]), [1]);
    const featResult = await featureSession.run({ waveforms, waveforms_lens });
    const features = featResult.features;
    const featuresLens = featResult.features_lens;

    // 2. Энкодер: mel → состояния
    const encResult = await encoderSession.run({
        audio_signal: features,
        length: featuresLens,
    });
    const encoderOutputs = encResult.outputs;
    const encodedLengths = encResult.encoded_lengths;

    const seqLen = Number(encodedLengths.data[0]);
    const hiddenSize = encoderOutputs.dims[2];

    // 3. TDT-декодирование: авторегрессионный цикл
    // Энкодер выдаёт [batch, hidden=1024, time] — канало-первый формат.
    const tokens = [];
    const timestamps = [];
    const encDims = encoderOutputs.dims; // [batch, hidden, time]
    const encData = encoderOutputs.data;
    const predHidden = 640; // Parakeet TDT 0.6B prediction network
    let prevToken = BLANK_ID;
    let state1 = new ort.Tensor('float32', new Float32Array(2 * predHidden), [2, 1, predHidden]);
    let state2 = new ort.Tensor('float32', new Float32Array(2 * predHidden), [2, 1, predHidden]);

    for (let frameIdx = 0; frameIdx < seqLen; frameIdx++) {
        // Извлекаем кадр: [1, hidden, 1] — один тайм-степ по каналам
        const frameData = new Float32Array(encDims[1]);
        for (let ch = 0; ch < encDims[1]; ch++) {
            frameData[ch] = encData[ch * seqLen + frameIdx];
        }
        const encFrame = new ort.Tensor('float32', frameData, [1, encDims[1], 1]);

        // targets: int32, [batch, 1]
        const targets = new ort.Tensor('int32', Int32Array.from([prevToken]), [1, 1]);
        const targetLength = new ort.Tensor('int32', Int32Array.from([1]), [1]);

        const decResult = await decoderSession.run({
            encoder_outputs: encFrame,
            targets,
            target_length: targetLength,
            input_states_1: state1,
            input_states_2: state2,
        });

        // Обновляем состояния
        state1 = decResult.output_states_1;
        state2 = decResult.output_states_2;

        // Логиты → argmax (только словарные индексы, не длительность)
        const logitData = decResult.outputs.data;
        const vocabLimit = Math.min(logitData.length, idToToken.size);
        let bestId = 0, bestVal = -Infinity;
        for (let i = 0; i < vocabLimit; i++) {
            if (logitData[i] > bestVal) { bestVal = logitData[i]; bestId = i; }
        }

        if (bestId !== BLANK_ID && bestId !== 0) {
            tokens.push(bestId);
            timestamps.push(frameIdx * FRAME_SECONDS);
        }
        prevToken = bestId;

        if (tokens.length > 2000) break;
    }

    // 4. Декодирование токенов → текст + сегменты
    return decodeTokens(tokens, timestamps);
}

function decodeTokens(tokens, timestamps) {
    const segments = [];
    let currentText = '';
    let currentStart = timestamps[0] ?? 0;

    for (let i = 0; i < tokens.length; i++) {
        const token = idToToken.get(tokens[i]) ?? '';
        const text = token.replace(/▁/g, ' ');

        // Проверка на спецтокены (пунктуация, эмоции и т.д.)
        if (token.startsWith('<|') && token.endsWith('>')) {
            // <|pnc|>, <|nopnc|> и прочие — пропускаем или обрабатываем
            continue;
        }

        currentText += text;

        // Разбиваем на сегменты по паузам (если следующая метка времени
        // сильно отличается от ожидаемой)
        const nextTime = timestamps[i + 1] ?? Infinity;
        const thisTime = timestamps[i] ?? 0;
        if (nextTime - thisTime > FRAME_SECONDS * 3 && currentText.trim()) {
            segments.push({
                start: currentStart,
                end: nextTime,
                text: currentText.trim(),
            });
            currentText = '';
            currentStart = nextTime;
        }
    }

    if (currentText.trim()) {
        segments.push({
            start: currentStart,
            end: (timestamps[tokens.length - 1] ?? 0) + FRAME_SECONDS,
            text: currentText.trim(),
        });
    }

    const fullText = segments.map(s => s.text).join(' ');
    return { text: fullText, segments };
}

// ─── Протокол stdin/stdout ─────────────────────────────────────────────────
async function main() {
    await initialize();

    const input = readline.createInterface({ input: process.stdin, terminal: false });

    for await (const line of input) {
        try {
            const request = JSON.parse(line);
            if (request.audio && request.sampleRate) {
                const result = await transcribe(request.audio);
                process.stdout.write(JSON.stringify({
                    id: request.id ?? '',
                    text: result.text,
                    segments: result.segments,
                    error: '',
                }) + '\n');
            } else {
                process.stdout.write(JSON.stringify({
                    id: request.id ?? '',
                    text: '',
                    segments: [],
                    error: 'нет аудиоданных',
                }) + '\n');
            }
        } catch (error) {
            process.stdout.write(JSON.stringify({
                id: '',
                text: '',
                segments: [],
                error: String(error?.message ?? error),
            }) + '\n');
        }
    }
}

main().catch(e => {
    process.stderr.write('parakeet: ' + e.message + '\n');
    process.exit(1);
});
