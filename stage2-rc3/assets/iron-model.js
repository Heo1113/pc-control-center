/*
 * IRON Stage 2 RC3 — HWiNFO REST read/sdb → IRON 모델 (순수 함수, 브라우저와 Node 공용, 외부 의존성 없음)
 *
 * 규칙의 출처
 *  - 형식: 공식 HWiNFO_REST_API_SKILL.md (malikm/HWiNFO@97f5d5d) — Version 1, type 키 0..8, Value는 문자열(천 단위 쉼표 제거),
 *          SDB 메타 객체는 정확히 tSensorLocation · tReadingLocation · tReadingType.
 *  - 경고 기준: config/iron_stage1_thresholds.json (IRON Stage 1 소스에서 추출).
 *  - 매핑: config/mapping_rules.json (이름은 후보만, 채택은 SDB 단위가 맞을 때만).
 *  - 신선도·게이트: config/gates.json.
 * 같은 규칙의 .NET 구현(poc/HWiNfoRestPoC)과 결과가 같아야 한다(tests/test_rc3_conformance.py).
 */
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.IronModel = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  // LIVE: 브라우저가 지금 폴링 중인 값. LIVE_CAPTURE: 수집기가 HWiNFO64 소유 포트에서 기록한 실측 파일.
  // UNVERIFIED_ENDPOINT: 수집기가 기록했으나 응답한 프로세스가 HWiNFO64인지 확인하지 못한 파일. OFFLINE_JSON: 사용자가 연 JSON 파일.
  // SYNTHETIC: 합성 데이터. 게이트 PASS는 LIVE_CAPTURE만 가능하다.
  var SOURCES = ['LIVE', 'LIVE_CAPTURE', 'UNVERIFIED_ENDPOINT', 'OFFLINE_JSON', 'SYNTHETIC'];
  var SDB_META_KEYS = { tSensorLocation: true, tReadingLocation: true, tReadingType: true };
  var TYPE_NAMES = { '0': 'NONE', '1': 'TEMP', '2': 'VOLT', '3': 'FAN', '4': 'CURRENT', '5': 'POWER', '6': 'CLOCK', '7': 'USAGE', '8': 'OTHER' };

  // 쉼표 뒤가 1~2자리로 끝나면 천 단위 구분이 아니라 소수점 쉼표일 가능성(예: "64,8")
  var DECIMAL_COMMA = /,[0-9]{1,2}$/;

  function isObj(v) { return v !== null && typeof v === 'object' && !Array.isArray(v); }
  function round(v, d) { if (v === null || v === undefined || !isFinite(v)) return null; var f = Math.pow(10, d); return Math.round(v * f) / f; }

  function checkVersion(json, kind) {
    if (!isObj(json)) throw new Error(kind + ': JSON 객체가 아닙니다.');
    if (!Object.prototype.hasOwnProperty.call(json, 'Version')) throw new Error(kind + ": 'Version' 필드가 없습니다.");
    if (json.Version !== 1) throw new Error(kind + ': 지원하지 않는 Version ' + String(json.Version) + ' (지원: 1)');
  }

  /** Value 문자열 → 숫자. 공식 문서대로 쉼표(천 단위)를 제거한다. 숫자가 아니면 null(원문은 raw에 남김). */
  function parseValue(raw) {
    var s = String(raw).replace(/,/g, '').trim();
    if (!/^[+-]?(\d+(\.\d*)?|\.\d+)([eE][+-]?\d+)?$/.test(s)) return null;
    var n = Number(s);
    return isFinite(n) ? n : null;
  }

  /** GET /api/sensors/read → { ts: {rs, ts, re}, rows } */
  function parseRead(json) {
    checkVersion(json, 'read');
    var rows = [];
    function walk(sensor, node, path) {
      Object.keys(node).forEach(function (k) {
        var v = node[k];
        if (!isObj(v)) return;
        if (Object.prototype.hasOwnProperty.call(v, 'Value')) {
          var isType = /^\d+$/.test(k);
          var rPath = isType ? path : path.concat([k]);
          rows.push({
            sensor: sensor,
            path: rPath.join(' / '),
            name: rPath.length ? rPath[rPath.length - 1] : '',
            typeKey: isType ? k : '',
            raw: String(v.Value),
            num: parseValue(v.Value)
          });
        } else {
          walk(sensor, v, path.concat([k]));
        }
      });
    }
    Object.keys(json).forEach(function (sensor) { if (isObj(json[sensor])) walk(sensor, json[sensor], []); });
    var t = function (k) { return typeof json[k] === 'number' && isFinite(json[k]) ? json[k] : null; };
    return { ts: { rs: t('Timestamp RS ms'), ts: t('Timestamp ms'), re: t('Timestamp RE ms') }, timestampText: json.Timestamp ? String(json.Timestamp) : null, rows: rows };
  }

  /** GET /api/sensors/sdb → 키("센서|경로|타입") → { unit, eType, hide, monitoring } */
  function parseSdb(json) {
    checkVersion(json, 'sdb');
    var index = {};
    function walk(sensor, node, path) {
      Object.keys(node).forEach(function (k) {
        var v = node[k];
        if (!isObj(v) || SDB_META_KEYS[k]) return;
        if (Object.prototype.hasOwnProperty.call(v, 'hide')) {
          var isType = /^\d+$/.test(k);
          var rPath = isType ? path : path.concat([k]);
          var key = sensor + '|' + rPath.join(' / ') + '|' + (isType ? k : '');
          index[key] = {
            unit: typeof v.Unit === 'string' ? v.Unit : '',
            eType: isObj(v.tReadingType) && typeof v.tReadingType.eType === 'number' ? v.tReadingType.eType : null,
            hide: v.hide === 1 || v.hide === true,
            monitoring: !(v.monitoring === 0 || v.monitoring === false)
          };
        } else {
          walk(sensor, v, path.concat([k]));
        }
      });
    }
    Object.keys(json).forEach(function (sensor) { if (isObj(json[sensor])) walk(sensor, json[sensor], []); });
    return index;
  }

  function keyOf(row) { return row.sensor + '|' + row.path + '|' + row.typeKey; }

  function normUnit(unit, rules) {
    var u = String(unit || '').trim();
    var canon = Object.keys(rules.units);
    for (var i = 0; i < canon.length; i++) {
      if (rules.units[canon[i]].indexOf(u) >= 0) return canon[i];
    }
    return u === '' ? '' : '?' + u;
  }

  /** 값을 기본 단위로: MB 기준(GB → ×1024), °C 기준(°F → 변환). */
  function toBase(num, unit) {
    if (num === null) return null;
    if (unit === 'GB') return num * 1024;
    if (unit === '°F') return (num - 32) * 5 / 9;
    return num;
  }

  /**
   * 한 프레임(read 1회)을 IRON 모델로. 상태가 없는 함수다(래치·신선도는 createTracker).
   * opts: { source, rules, thresholds }
   */
  /** Stage 1 NumericSetting과 같은 검사: 숫자, 범위, 소수 자릿수. 통과하면 { value }, 아니면 { error }. */
  function validateSetting(text, spec) {
    var t = String(text === null || text === undefined ? '' : text).trim();
    var range = spec.min.toFixed(spec.decimals) + '~' + spec.max.toFixed(spec.decimals);
    if (t === '') return { error: '값을 입력하세요 (' + range + ').' };
    if (!/^[+-]?(\d+(\.\d*)?|\.\d+)$/.test(t)) return { error: '숫자로 입력하세요 (' + range + ').' };
    var dot = t.indexOf('.');
    if (dot >= 0 && t.length - dot - 1 > spec.decimals) return { error: spec.decimals === 0 ? '정수로 입력하세요 (' + range + ').' : '소수 ' + spec.decimals + '자리까지 입력하세요 (' + range + ').' };
    var v = Number(t);
    if (v < spec.min || v > spec.max) return { error: range + ' 범위로 입력하세요.' };
    return { value: v };
  }

  /** 적용할 UI 주의 기준. opts.limits 가 없으면 Stage 1 기본값. 범위를 벗어난 값은 거부(예외). */
  function limitsOf(opts) {
    var th = opts.thresholds, l = opts.limits || {};
    var g = l.gpuTempC === undefined || l.gpuTempC === null ? th.gpuTemperatureC['default'] : l.gpuTempC;
    var p = l.pinA === undefined || l.pinA === null ? th.wireViewPinCurrentA['default'] : l.pinA;
    if (validateSetting(String(g), th.gpuTemperatureC).error) throw new Error('GPU 온도 기준이 Stage 1 범위 밖: ' + g);
    if (validateSetting(String(p), th.wireViewPinCurrentA).error) throw new Error('핀 전류 기준이 Stage 1 범위 밖: ' + p);
    return { gpuTempC: g, pinA: p };
  }

  function mapFrame(read, sdbIndex, opts) {
    if (SOURCES.indexOf(opts.source) < 0) throw new Error('알 수 없는 데이터 출처: ' + opts.source);
    var rules = opts.rules, th = opts.thresholds, lim = limitsOf(opts);
    var mapped = 0, unmapped = [], mismatches = [], candidates = {}, resolved = {}, commaSuspects = [];
    var rows = read.rows.map(function (r) {
      var meta = sdbIndex[keyOf(r)];
      if (meta) mapped++; else unmapped.push(keyOf(r));
      if (DECIMAL_COMMA.test(r.raw.trim())) commaSuspects.push(keyOf(r));
      return { sensor: r.sensor, path: r.path, name: r.name, typeKey: r.typeKey, raw: r.raw, num: r.num, unit: meta ? normUnit(meta.unit, rules) : null, key: keyOf(r) };
    });

    /** 규칙에 맞는 첫 리딩. 이름이 맞아도 단위가 다르면 거부 사유를 남긴다. */
    function pick(list, rule, label) {
      var re = new RegExp(rule.name);
      var found = null;
      list.forEach(function (r) {
        if (!re.test(r.name)) return;
        (candidates[label] = candidates[label] || []).push(r.key);
        if (r.unit === null) return; // SDB에 없음: unmapped로 따로 집계
        if (rule.units.indexOf(r.unit) < 0) { mismatches.push({ item: label, key: r.key, unit: r.unit, allowed: rule.units }); return; }
        if (found === null && r.num !== null) found = r;
      });
      resolved[label] = found ? found.key : null;
      return found;
    }
    function val(r, decimals) { return r ? round(toBase(r.num, r.unit), decimals) : null; }
    function groupBy(sensorRe) {
      var re = new RegExp(sensorRe);
      var map = {};
      rows.forEach(function (r) { var m = re.exec(r.sensor); if (m) (map[r.sensor] = map[r.sensor] || { m: m, rows: [] }).rows.push(r); });
      return map;
    }

    // CPU
    var g = rules.groups;
    var cpuGroups = groupBy(g.cpu.sensor);
    var cpuName = Object.keys(cpuGroups)[0] || null;
    var cpuRows = cpuName ? cpuGroups[cpuName].rows : [];
    var cpu = {
      name: cpuName ? cpuName.replace(/^CPU \[#\d+\]:\s*/, '').trim() : null,
      tctlC: val(pick(cpuRows, g.cpu.readings.tctl, 'cpu.tctl'), 1),
      packagePowerW: val(pick(cpuRows, g.cpu.readings.packagePower, 'cpu.packagePower'), 1),
      usagePct: val(pick(cpuRows, g.cpu.readings.usage, 'cpu.usage'), 1)
    };

    // GPUs: HWiNFO가 한 GPU를 여러 센서 그룹으로 낼 수 있으므로 [#n] 번호로 묶는다.
    var gpuGroups = groupBy(g.gpu.sensor);
    var byIndex = {};
    Object.keys(gpuGroups).forEach(function (s) {
      var idx = Number(gpuGroups[s].m[1]);
      var e = byIndex[idx] = byIndex[idx] || { index: idx, names: [], rows: [] };
      e.names.push(s);
      e.rows = e.rows.concat(gpuGroups[s].rows);
    });
    // SDB에만 있는 GPU도 "있어야 하는 GPU"로 센다(합계 PARTIAL 판정).
    var sdbGpuIdx = {};
    var gre = new RegExp(g.gpu.sensor);
    Object.keys(sdbIndex).forEach(function (k) { var m = gre.exec(k.split('|')[0]); if (m) sdbGpuIdx[Number(m[1])] = true; });
    Object.keys(byIndex).forEach(function (k) { sdbGpuIdx[Number(k)] = true; });
    var gpuIdx = Object.keys(sdbGpuIdx).map(Number).sort(function (a, b) { return a - b; });
    var gpus = gpuIdx.map(function (i) {
      var e = byIndex[i] || { index: i, names: [], rows: [] };
      var R = g.gpu.readings, L = 'gpu[' + i + '].';
      var core = val(pick(e.rows, R.coreTemp, L + 'coreTemp'), 1);
      return {
        index: i,
        name: e.names.length ? e.names[0].replace(/^GPU \[#\d+\]:\s*/, '').trim() : null,
        coreTempC: core,
        hotspotC: val(pick(e.rows, R.hotspotTemp, L + 'hotspotTemp'), 1),
        memJunctionC: val(pick(e.rows, R.memJunctionTemp, L + 'memJunctionTemp'), 1),
        powerW: val(pick(e.rows, R.power, L + 'power'), 1),
        loadPct: val(pick(e.rows, R.load, L + 'load'), 1),
        vramUsedMB: val(pick(e.rows, R.vramUsed, L + 'vramUsed'), 0),
        clockMHz: val(pick(e.rows, R.clock, L + 'clock'), 0),
        // Stage 1 규칙: 코어 온도만 비교. 핫스팟으로 대신하지 않는다.
        hot: core === null ? null : core >= lim.gpuTempC
      };
    });
    var powers = gpus.map(function (x) { return x.powerW; });
    var reported = powers.filter(function (p) { return p !== null && p >= 0; }).length;
    var gpuPower = {
      state: gpus.length === 0 || reported === 0 ? 'UNAVAILABLE' : reported === gpus.length ? 'COMPLETE' : 'PARTIAL',
      totalW: null, reported: reported, expected: gpus.length
    };
    if (gpuPower.state === 'COMPLETE') gpuPower.totalW = round(powers.reduce(function (a, b) { return a + b; }, 0), 1);
    function maxOf(key) { var v = gpus.map(function (x) { return x[key]; }).filter(function (x) { return x !== null; }); return v.length ? Math.max.apply(null, v) : null; }
    var gpuTemps = { coreMaxC: maxOf('coreTempC'), hotspotMaxC: maxOf('hotspotC'), memJunctionMaxC: maxOf('memJunctionC') };

    // RAM
    var ramGroups = groupBy(g.ram.sensor);
    var ramRows = [];
    Object.keys(ramGroups).forEach(function (s) { ramRows = ramRows.concat(ramGroups[s].rows); });
    var ram = {
      usedMB: val(pick(ramRows, g.ram.readings.usedMB, 'ram.usedMB'), 0),
      availableMB: val(pick(ramRows, g.ram.readings.availableMB, 'ram.availableMB'), 0),
      loadPct: val(pick(ramRows, g.ram.readings.loadPct, 'ram.loadPct'), 1)
    };

    // DIMM / NVMe (여러 개)
    function many(groupRule, readingRules, labelPrefix) {
      var groups = groupBy(groupRule.sensor);
      return Object.keys(groups).map(function (s) {
        var out = { sensor: s };
        Object.keys(readingRules).forEach(function (k) { out[k] = val(pick(groups[s].rows, readingRules[k], labelPrefix + '.' + k), 1); });
        return out;
      });
    }
    var dimms = many(g.dimm, g.dimm.readings, 'dimm');
    var nvme = many(g.nvme, g.nvme.readings, 'nvme');

    // WireView: 핀 번호와 단위로 전류·전압·전력을 가른다.
    var wvRule = g.wireview;
    var wvRe = new RegExp(wvRule.sensor);
    var wvRows = rows.filter(function (r) { return wvRe.test(r.sensor); });
    var wireview = { present: wvRows.length > 0, completeness: 'ABSENT', presentPins: 0, pins: [], totalW: null, temperaturesC: [], deltaA: null, ownership: { state: 'UNCONFIRMED', basis: 'REST 센서 노출만으로는 USB 소유권을 확정하지 않음 — 운영자 확인·프로세스·PnP 증거 필요' } };
    if (wireview.present) {
      var pinRe = new RegExp(wvRule.pinName);
      var pins = [1, 2, 3, 4, 5, 6].map(function (n) { return { pin: n, currentA: null, voltageV: null, powerW: null, level: 'N/A' }; });
      wvRows.forEach(function (r) {
        var m = pinRe.exec(r.path);
        if (!m) return;
        var p = pins[Number(m[1]) - 1];
        if (r.unit === null || r.num === null) return;
        if (wvRule.pinUnits.currentA.indexOf(r.unit) >= 0 && p.currentA === null) { p.currentA = round(r.num, 2); resolved['wireview.pin' + m[1]] = r.key; }
        else if (wvRule.pinUnits.voltageV.indexOf(r.unit) >= 0 && p.voltageV === null) p.voltageV = round(r.num, 2);
        else if (wvRule.pinUnits.powerW.indexOf(r.unit) >= 0 && p.powerW === null) p.powerW = round(r.num, 1);
        else if (wvRule.pinUnits.currentA.indexOf(r.unit) < 0 && wvRule.pinUnits.voltageV.indexOf(r.unit) < 0 && wvRule.pinUnits.powerW.indexOf(r.unit) < 0) {
          mismatches.push({ item: 'wireview.pin' + m[1], key: r.key, unit: r.unit, allowed: ['A', 'V', 'W'] });
        }
      });
      var limit = lim.pinA;
      pins.forEach(function (p) {
        if (p.currentA === null) return;
        p.level = p.currentA >= limit ? 'OVER' : p.currentA >= limit * th.pinApproachingShare ? 'APPROACHING' : 'NORMAL';
      });
      var amps = pins.map(function (p) { return p.currentA; }).filter(function (a) { return a !== null; });
      pins.forEach(function (p) { if (p.currentA === null) resolved['wireview.pin' + p.pin] = null; });
      wireview.pins = pins;
      wireview.presentPins = amps.length;
      wireview.completeness = amps.length === 6 ? 'COMPLETE' : 'INCOMPLETE';
      // 6핀이 모두 있을 때만 편차(최대-최소)를 낸다. 일부만으로 낸 편차는 실제보다 작을 수 있다.
      wireview.deltaA = amps.length === 6 ? round(Math.max.apply(null, amps) - Math.min.apply(null, amps), 2) : null;
      var tot = pick(wvRows.filter(function (r) { return !pinRe.test(r.path); }), wvRule.totalPower, 'wireview.totalPower');
      wireview.totalW = tot ? round(tot.num, 1) : null;
      var tRe = new RegExp(wvRule.temperature.name);
      wireview.temperaturesC = wvRows.filter(function (r) { return tRe.test(r.name) && wvRule.temperature.units.indexOf(r.unit) >= 0 && r.num !== null; })
        .map(function (r) { return { name: r.path, valueC: round(toBase(r.num, r.unit), 1) }; });
      wireview.anyPinOver = pins.some(function (p) { return p.level === 'OVER'; });
    }

    return {
      source: opts.source,
      limits: lim,
      hwinfo: {
        timestampMs: read.ts.ts, timestampRsMs: read.ts.rs, timestampReMs: read.ts.re,
        readoutMs: read.ts.ts !== null && read.ts.rs !== null ? round(read.ts.ts - read.ts.rs, 1) : null,
        jsonBuildMs: read.ts.re !== null && read.ts.ts !== null ? round(read.ts.re - read.ts.ts, 1) : null,
        ordered: read.ts.rs !== null && read.ts.ts !== null && read.ts.re !== null && read.ts.rs > 0 && read.ts.rs <= read.ts.ts && read.ts.ts <= read.ts.re
      },
      cpu: cpu, gpus: gpus, gpuPower: gpuPower, gpuTemps: gpuTemps, ram: ram, dimms: dimms, nvme: nvme, wireview: wireview,
      mapping: {
        readings: rows.length, mappedToSdb: mapped,
        mappingPct: rows.length ? round(mapped * 100 / rows.length, 2) : 0,
        unmappedKeys: unmapped.slice(0, 50), unitMismatches: mismatches, candidates: candidates, resolved: resolved,
        decimalCommaSuspects: commaSuspects.slice(0, 50), decimalCommaSuspectCount: commaSuspects.length
      }
    };
  }

  /**
   * 프레임 사이의 상태: HWiNFO 타임스탬프 기반 신선도와 Stage 1 핀 래치.
   * 래치는 '서로 다른 HWiNFO 타임스탬프'만 센다(1초 폴링·2초 갱신이면 같은 측정이 두 번 오므로).
   */
  function createTracker(opts) {
    var th = opts.thresholds, fresh = opts.gates.freshness;
    var st = { lastTs: null, lastAdvanceAt: null, pinOverCount: 0, pinLatched: false, offsetMs: null, invalid: 0 };
    return {
      /** /api/ping 의 t 와 요청 전후 시각(t0,t3)으로 서버-클라이언트 시계 차이를 잡는다(공식 문서의 방식). */
      setClockOffset: function (pingT, t0, t3) { st.offsetMs = pingT - (t0 + t3) / 2; },
      /** 새 응답이 없을 때(요청 실패·지연)의 신선도: 마지막으로 타임스탬프가 증가한 뒤 경과 시간으로만 본다. */
      check: function (nowMs) {
        if (st.lastAdvanceAt === null) return { freshness: 'UNKNOWN', frozenMs: null };
        var f = nowMs - st.lastAdvanceAt;
        if (f > fresh.maxFrozenMs) { st.pinOverCount = 0; st.pinLatched = false; }
        return { freshness: f > fresh.maxFrozenMs ? 'STALE' : 'PENDING', frozenMs: f };
      },
      invalidCount: function () { return st.invalid; },
      update: function (model, receivedAtMs) {
        var h = model.hwinfo;
        var out = { freshness: 'UNKNOWN', ageMs: null, frozenMs: null, reasons: [], newMeasurement: false, pinAlert: false, pinOverCount: 0 };
        if (!h.ordered || h.timestampMs === null || (st.lastTs !== null && h.timestampMs < st.lastTs)) {
          st.invalid++;
          out.freshness = 'INVALID';
          out.reasons.push('타임스탬프 순서 오류(RS<=TS<=RE 위반 또는 역행) — 프레임 폐기');
          return out;
        }
        if (st.lastTs === null || h.timestampMs > st.lastTs) {
          out.newMeasurement = true;
          st.lastTs = h.timestampMs;
          st.lastAdvanceAt = receivedAtMs;
        }
        out.frozenMs = receivedAtMs - st.lastAdvanceAt;
        if (st.offsetMs !== null) out.ageMs = round(receivedAtMs + st.offsetMs - h.timestampMs, 0);
        var stale = false;
        if (out.frozenMs > fresh.maxFrozenMs) { stale = true; out.reasons.push('HWiNFO 타임스탬프가 ' + out.frozenMs + ' ms 동안 그대로'); }
        if (out.ageMs !== null && out.ageMs > fresh.maxAgeMs) { stale = true; out.reasons.push('측정 후 ' + out.ageMs + ' ms 경과'); }
        out.freshness = stale ? 'STALE' : (out.ageMs === null ? 'FRESH_BY_PROGRESS' : 'FRESH');

        // 핀 래치: 6핀이 모두 있든 없든 '측정된' 핀이 기준 이상이면 센다(숨기지 않음). 새 측정만 센다. STALE·ABSENT면 해제.
        var wv = model.wireview;
        if (stale || !wv.present || wv.presentPins === 0) { st.pinOverCount = 0; st.pinLatched = false; }
        else if (out.newMeasurement) {
          if (wv.anyPinOver) { st.pinOverCount = Math.min(th.pinLatchReadings, st.pinOverCount + 1); if (st.pinOverCount >= th.pinLatchReadings) st.pinLatched = true; }
          else { st.pinOverCount = 0; st.pinLatched = false; }
        }
        out.pinAlert = st.pinLatched;
        out.pinOverCount = st.pinOverCount;
        return out;
      }
    };
  }

  /** 표시용 요약: Stage 1과 같은 상태 문구, WireView 상태(INCOMPLETE는 OK가 아님). */
  function summarize(model, tracked) {
    var gpuHot = model.gpus.some(function (g) { return g.hot === true; });
    var wv = model.wireview;
    var wvStatus = tracked.freshness === 'STALE' || tracked.freshness === 'INVALID' ? tracked.freshness : !wv.present ? 'N/A' : wv.completeness === 'INCOMPLETE' ? 'INCOMPLETE (' + wv.presentPins + '/6)' : tracked.pinAlert ? '핀 불균형 주의' : 'OK';
    var status = tracked.freshness === 'STALE' ? 'STALE' : tracked.freshness === 'INVALID' ? 'INVALID'
      : tracked.pinAlert && gpuHot ? '핀 불균형 · GPU 온도 주의' : tracked.pinAlert ? '핀 불균형 주의' : gpuHot ? 'GPU 온도 주의' : 'ONLINE';
    return { source: model.source, status: status, wireViewStatus: wvStatus, gpuPowerText: model.gpuPower.state === 'COMPLETE' ? model.gpuPower.totalW + ' W' : model.gpuPower.state === 'PARTIAL' ? '일부 미확인' : 'N/A' };
  }

  /**
   * G2 기준(config/gates.json)으로 한 프레임을 평가한다. 단위 불일치는 '그 항목이 끝내 채워지지 않은 경우'만 실패로 센다
   * (예: 같은 이름의 % 리딩이 있어도 W 리딩이 채택되면 통과, 기록은 남김).
   * verdict: LIVE_CAPTURE에서만 PASS/FAIL, 그 밖의 출처는 기준 충족 여부와 무관하게 UNVERIFIED.
   */
  function evaluateG2(model, gates) {
    var G = gates.G2, checks = [];
    function add(name, value, limit, pass) { checks.push({ name: name, value: value, limit: limit, pass: pass }); }
    var res = model.mapping.resolved;
    var blocking = model.mapping.unitMismatches.filter(function (m) { return res[m.item] === null || res[m.item] === undefined; });
    add('readToSdbMappingPct', model.mapping.mappingPct, '>= ' + G.readToSdbMappingPctMin, model.mapping.mappingPct >= G.readToSdbMappingPctMin);
    add('blockingUnitMismatches', blocking.length, '<= ' + G.unitMismatchesMax, blocking.length <= G.unitMismatchesMax);
    add('decimalCommaSuspects', model.mapping.decimalCommaSuspectCount, '<= ' + G.decimalCommaSuspectsMax, model.mapping.decimalCommaSuspectCount <= G.decimalCommaSuspectsMax);
    add('gpuCount', model.gpus.length, '== ' + G.expectedGpuCount, model.gpus.length === G.expectedGpuCount);
    var cpuField = { tctl: 'tctlC', usage: 'usagePct', packagePower: 'packagePowerW' };
    var gpuField = { coreTemp: 'coreTempC', power: 'powerW', load: 'loadPct', hotspotTemp: 'hotspotC', memJunctionTemp: 'memJunctionC', vramUsed: 'vramUsedMB', clock: 'clockMHz' };
    G.requiredCpu.forEach(function (k) { var v = model.cpu[cpuField[k]]; add('cpu.' + k, v, 'present', v !== null); });
    model.gpus.forEach(function (g) {
      G.requiredPerGpu.forEach(function (k) { var v = g[gpuField[k]]; add('gpu[' + g.index + '].' + k, v, 'present', v !== null); });
    });
    G.requiredRam.forEach(function (k) { var v = model.ram[k]; add('ram.' + k, v, 'present', v !== null); });
    var info = {};
    G.informational.forEach(function (k) {
      var parts = k.split('.'), grp = parts[0], f = parts[1];
      if (grp === 'cpu') info[k] = model.cpu[cpuField[f]] !== null;
      else if (grp === 'gpu') info[k] = model.gpus.map(function (g) { return g[gpuField[f]] !== null; });
      else if (grp === 'dimm' || grp === 'nvme') info[k] = model[grp === 'dimm' ? 'dimms' : 'nvme'].map(function (d) { return d[f] !== null; });
    });
    var met = checks.every(function (c) { return c.pass; });
    return {
      gate: 'G2', source: model.source, criteriaMet: met,
      verdict: model.source === 'LIVE_CAPTURE' ? (met ? 'PASS' : 'FAIL') : 'UNVERIFIED',
      checks: checks, blockingUnitMismatches: blocking, informational: info
    };
  }

  return { evaluateG2: evaluateG2, validateSetting: validateSetting, limitsOf: limitsOf, SOURCES: SOURCES, TYPE_NAMES: TYPE_NAMES, SDB_META_KEYS: Object.keys(SDB_META_KEYS), parseValue: parseValue, parseRead: parseRead, parseSdb: parseSdb, normUnit: normUnit, mapFrame: mapFrame, createTracker: createTracker, summarize: summarize };
});
