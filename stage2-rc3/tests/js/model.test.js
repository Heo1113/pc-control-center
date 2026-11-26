'use strict';
// node --test tests/js/  — 공유 모델의 규칙을 값으로 확인한다(합성 픽스처, 실측 아님).
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('path');
const fs = require('fs');
const ROOT = path.resolve(__dirname, '..', '..');
const M = require(path.join(ROOT, 'assets', 'iron-model.js'));
const R = require(path.join(ROOT, 'tools', 'run_model.js'));
const { rules, thresholds, gates } = R.cfg;
const fx = (n) => JSON.parse(fs.readFileSync(path.join(ROOT, 'tests', 'fixtures', n), 'utf8'));

test('Stage 1 기준을 그대로 쓴다 (72 °C, 7.0 A, 0.72, 3회)', () => {
  assert.equal(thresholds.gpuTemperatureC.default, 72);
  assert.equal(thresholds.wireViewPinCurrentA.default, 7.0);
  assert.equal(thresholds.pinApproachingShare, 0.72);
  assert.equal(thresholds.pinLatchReadings, 3);
  const m = R.frameResult('synthetic_full').model;
  assert.deepEqual(m.limits, { gpuTempC: 72, pinA: 7 });
});

test('완전한 데이터: 6핀 COMPLETE, 합계 COMPLETE, 핫스팟은 경고에 쓰지 않음, 단위 변환', () => {
  const { model: m, g2 } = R.frameResult('synthetic_full');
  assert.equal(m.wireview.completeness, 'COMPLETE');
  assert.equal(m.wireview.presentPins, 6);
  assert.equal(m.wireview.deltaA, 0.31);
  assert.equal(m.gpuPower.state, 'COMPLETE');
  assert.equal(m.gpuPower.totalW, 393.7);
  assert.equal(m.gpus[0].hotspotC, 78.9); // 72 이상이지만
  assert.equal(m.gpus[0].hot, false); // 코어 66.5 → 경고 아님
  assert.equal(m.gpuTemps.coreMaxC, 66.5);
  assert.equal(m.gpuTemps.hotspotMaxC, 78.9);
  assert.equal(m.gpus[1].vramUsedMB, 1536); // 1.5 GB
  assert.equal(m.ram.usedMB, 36864); // "36,864"
  assert.equal(m.wireview.ownership.state, 'UNCONFIRMED');
  assert.equal(g2.criteriaMet, true);
  assert.equal(g2.verdict, 'UNVERIFIED'); // 합성 데이터는 기준을 넘어도 PASS가 아니다
});

test('RC2 합성 픽스처: 핀 1개뿐이면 INCOMPLETE, 편차·OK 없음, GPU 합계 PARTIAL', () => {
  const { model: m } = R.frameResult('rc2_synthetic');
  assert.equal(m.wireview.completeness, 'INCOMPLETE');
  assert.equal(m.wireview.presentPins, 1);
  assert.equal(m.wireview.deltaA, null);
  assert.equal(m.gpuPower.state, 'PARTIAL');
  assert.equal(m.gpuPower.totalW, null);
  const s = M.summarize(m, { freshness: 'FRESH', pinAlert: false });
  assert.equal(s.wireViewStatus, 'INCOMPLETE (1/6)');
  assert.notEqual(s.wireViewStatus, 'OK');
  assert.equal(s.gpuPowerText, '일부 미확인');
});

test('SDB 단위가 다르면 이름이 같아도 채택하지 않는다', () => {
  const { model: m, g2 } = R.frameResult('synthetic_mismatch');
  assert.equal(m.gpus[1].powerW, null);
  assert.equal(m.mapping.unitMismatches.length, 1);
  assert.equal(m.mapping.unitMismatches[0].unit, '%');
  assert.equal(m.gpuPower.state, 'PARTIAL');
  assert.equal(m.mapping.mappingPct, 97.22);
  assert.equal(g2.criteriaMet, false);
  assert.deepEqual(g2.checks.filter((c) => !c.pass).map((c) => c.name), ['readToSdbMappingPct', 'blockingUnitMismatches', 'decimalCommaSuspects', 'gpu[1].power']);
  // 소수점 쉼표 "46,5"는 쉼표 제거 규칙으로 465가 된다 — 값은 그대로 두되 의심 목록으로 G2를 막는다
  assert.equal(m.dimms[0].temp, 465);
  assert.deepEqual(m.mapping.decimalCommaSuspects, ['DDR5 DIMM [#0] (SPD)|SPD Hub Temperature|1']);
});

test('래치는 서로 다른 HWiNFO 타임스탬프만 센다; WireView 해제는 GPU 경고에 영향 없음', () => {
  const f = R.seqResult('seq_latch_release').frames;
  assert.deepEqual(f.map((x) => x.pinAlert), [false, false, false, false, true, true, false, false, false, false, false]);
  assert.deepEqual(f.map((x) => x.pinOverCount), [1, 1, 2, 2, 3, 3, 0, 0, 1, 1, 0]);
  f.forEach((x) => assert.deepEqual(x.gpuHot, [true, false]));
  assert.equal(f[6].wireViewStatus, 'N/A');
  assert.equal(f[6].status, 'GPU 온도 주의');
  assert.equal(f[4].status, '핀 불균형 · GPU 온도 주의');
});

test('타임스탬프 정지 → STALE (6000 ms 초과)', () => {
  const f = R.seqResult('seq_frozen').frames;
  assert.equal(f[8].freshness, 'FRESH_BY_PROGRESS');
  assert.equal(f[8].frozenMs, 6000);
  assert.equal(f[9].freshness, 'STALE');
  assert.equal(f[9].wireViewStatus, 'STALE');
});

test('RS<=TS<=RE 위반·역행 → INVALID', () => {
  const r = R.seqResult('seq_invalid');
  assert.equal(r.invalidCount, 2);
  assert.deepEqual(r.frames.map((x) => x.freshness), ['FRESH_BY_PROGRESS', 'FRESH_BY_PROGRESS', 'INVALID', 'FRESH_BY_PROGRESS', 'INVALID', 'FRESH_BY_PROGRESS']);
});

test('ping 시계 보정 후 측정 나이로 STALE', () => {
  const f = R.seqResult('seq_age').frames;
  assert.deepEqual(f.map((x) => x.ageMs), [8050, 9050, 8050, 9050]);
  assert.ok(f.every((x) => x.freshness === 'STALE'));
});

test('응답이 끊기면 check()가 STALE을 낸다', () => {
  const tr = M.createTracker({ thresholds, gates });
  const m = R.frameResult('synthetic_full').model;
  tr.update(m, 1000);
  assert.equal(tr.check(7000).freshness, 'PENDING');
  assert.equal(tr.check(7001).freshness, 'STALE');
});

test('UI 기준 입력 검사는 Stage 1 NumericSetting과 같다', () => {
  const g = thresholds.gpuTemperatureC, p = thresholds.wireViewPinCurrentA;
  assert.equal(M.validateSetting('72', g).value, 72);
  assert.ok(M.validateSetting('72.5', g).error);
  assert.ok(M.validateSetting('96', g).error);
  assert.ok(M.validateSetting('54', g).error);
  assert.ok(M.validateSetting('', g).error);
  assert.ok(M.validateSetting('abc', g).error);
  assert.equal(M.validateSetting('6.5', p).value, 6.5);
  assert.ok(M.validateSetting('6.55', p).error);
  assert.ok(M.validateSetting('9.5', p).error);
  assert.throws(() => M.limitsOf({ thresholds, limits: { pinA: 9.5 } }));
  const read = M.parseRead(fx('seq_latch_release.json').frames[0].read);
  const sdb = M.parseSdb(fx('synthetic_full_sdb_v1.json'));
  const m = M.mapFrame(read, sdb, { source: 'SYNTHETIC', rules, thresholds, limits: { pinA: 8.0 } });
  assert.equal(m.wireview.anyPinOver, false); // 7.40 A < 8.0
  assert.equal(m.wireview.pins[2].level, 'APPROACHING'); // >= 8.0 x 0.72
});

test('값·형식·출처 검사', () => {
  assert.equal(M.parseValue('1,234.5'), 1234.5);
  assert.equal(M.parseValue('N/A'), null);
  assert.equal(M.parseValue('Yes'), null);
  assert.throws(() => M.parseRead({ Version: 2 }));
  assert.throws(() => M.parseSdb({}));
  const read = M.parseRead(fx('synthetic_full_read_v1.json'));
  assert.throws(() => M.mapFrame(read, {}, { source: 'LIVE?', rules, thresholds }));
  // SDB 메타 키는 정확히 세 개만 제외한다(이름이 t로 시작하는 리딩은 남긴다)
  const sdb = M.parseSdb({ Version: 1, S: { tReadingLocationX: { 1: { hide: 0, monitoring: 1, Unit: '°C', tReadingType: { eType: 1 } } }, tReadingType: { eType: 1 } } });
  assert.deepEqual(Object.keys(sdb), ['S|tReadingLocationX|1']);
});
