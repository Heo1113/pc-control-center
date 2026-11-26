#!/usr/bin/env node
/*
 * 공유 모델(assets/iron-model.js)을 픽스처에 돌려 결과를 JSON으로 낸다. .NET 구현(poc selftest)의 출력과 비교하는 기준.
 *   node tools/run_model.js            # tests/fixtures/expected/*.json 갱신
 *   node tools/run_model.js --check    # 다시 계산한 결과가 저장본과 같은지 확인(다르면 exit 1)
 *   node tools/run_model.js --print <fixture>   # 한 픽스처 결과를 출력
 */
'use strict';
const fs = require('fs');
const path = require('path');
const ROOT = path.resolve(__dirname, '..');
const M = require(path.join(ROOT, 'assets', 'iron-model.js'));
const load = (p) => JSON.parse(fs.readFileSync(path.join(ROOT, p), 'utf8'));
const cfg = { rules: load('config/mapping_rules.json'), thresholds: load('config/iron_stage1_thresholds.json'), gates: load('config/gates.json') };

const FRAMES = {
  synthetic_full: ['synthetic_full_read_v1.json', 'synthetic_full_sdb_v1.json'],
  synthetic_mismatch: ['synthetic_mismatch_read_v1.json', 'synthetic_mismatch_sdb_v1.json'],
  rc2_synthetic: ['synthetic_read_v1.json', 'synthetic_sdb_v1.json']
};
const SEQS = ['seq_latch_release', 'seq_frozen', 'seq_invalid', 'seq_age'];

function frameResult(name) {
  const [r, s] = FRAMES[name];
  const model = M.mapFrame(M.parseRead(load('tests/fixtures/' + r)), M.parseSdb(load('tests/fixtures/' + s)), { source: 'SYNTHETIC', rules: cfg.rules, thresholds: cfg.thresholds });
  return { fixture: name, model: model, g2: M.evaluateG2(model, cfg.gates) };
}

function seqResult(name) {
  const seq = load('tests/fixtures/' + name + '.json');
  const sdb = M.parseSdb(load('tests/fixtures/' + seq.sdb));
  const tr = M.createTracker({ thresholds: cfg.thresholds, gates: cfg.gates });
  if (seq.clock) tr.setClockOffset(seq.clock.pingT, seq.clock.t0, seq.clock.t3);
  const frames = seq.frames.map((f) => {
    const model = M.mapFrame(M.parseRead(f.read), sdb, { source: seq.source, rules: cfg.rules, thresholds: cfg.thresholds });
    const t = tr.update(model, f.receivedAtMs);
    const sum = M.summarize(model, t);
    return {
      i: f.i, freshness: t.freshness, ageMs: t.ageMs, frozenMs: t.frozenMs, newMeasurement: t.newMeasurement,
      pinOverCount: t.pinOverCount, pinAlert: t.pinAlert, gpuHot: model.gpus.map((g) => g.hot),
      wireViewCompleteness: model.wireview.completeness, status: sum.status, wireViewStatus: sum.wireViewStatus, reasons: t.reasons
    };
  });
  return { fixture: name, invalidCount: tr.invalidCount(), frames: frames };
}

function all() {
  const out = {};
  Object.keys(FRAMES).forEach((n) => { out[n + '.model.json'] = frameResult(n); });
  SEQS.forEach((n) => { out[n + '.seq.json'] = seqResult(n); });
  return out;
}

if (require.main === module) {
  const args = process.argv.slice(2);
  if (args[0] === '--print') {
    const n = args[1];
    console.log(JSON.stringify(FRAMES[n] ? frameResult(n) : seqResult(n), null, 1));
    process.exit(0);
  }
  const check = args.includes('--check');
  const dir = path.join(ROOT, 'tests', 'fixtures', 'expected');
  const changed = [];
  const res = all();
  Object.keys(res).forEach((f) => {
    const text = JSON.stringify(res[f], null, 1) + '\n';
    const p = path.join(dir, f);
    const old = fs.existsSync(p) ? fs.readFileSync(p, 'utf8') : null;
    if (old !== text) { changed.push(f); if (!check) fs.writeFileSync(p, text); }
  });
  console.log(check ? (changed.length ? '저장본과 다름: ' + changed.join(', ') : '저장본과 같음') : '갱신: ' + (changed.join(', ') || '없음'));
  process.exit(check && changed.length ? 1 : 0);
}
module.exports = { all, frameResult, seqResult, cfg, FRAMES, SEQS };
