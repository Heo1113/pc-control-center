/* 자동 생성 파일 — 직접 수정 금지. 원본: config/iron_stage1_thresholds.json, config/mapping_rules.json, config/gates.json
 * 생성: python -I tools/build_iron_config.py */
(function (root) { root.IRON_CONFIG = {
 "thresholds": {
  "_provenance": {
   "note": "IRON Stage 1 (feat/iron-ui-stage1) 소스에서 tools/extract_stage1_thresholds.py로 추출한 UI 주의 기준. Stage 1은 값을 저장하지 않으므로 '실제 설정' = 소스의 기본값·허용 범위. 하드웨어(WireView 펌웨어) 보호 임계값과 무관.",
   "repository": "Heo1113/pc-control-center",
   "branch": "feat/iron-ui-stage1",
   "commit": "187d5339a2dda4bfdf811b6dc8d3ead5944878e0",
   "files": {
    "src/Iron.App/Presentation/ShellViewModel.cs": "34-35 (NumericSetting 기본값·범위)",
    "src/Iron.App/Presentation/Alerts.cs": "26 PinLatchReadings, 29 ApproachingShare, 43-48 IsHot/LevelOf (>= 비교)",
    "src/Iron.App/Presentation/DeviceViewModels.cs": "35 PinScaleA"
   },
   "sha256": {
    "src/Iron.App/Presentation/ShellViewModel.cs": "e527719b95fdbfdc860ab5934b06ee76dd927cf90105b5fff4df238c8846f1e3",
    "src/Iron.App/Presentation/Alerts.cs": "b7e923720ca7b9499ddcc5cb35bbbc0b832697fbb347d0df962bc229dfb962a4",
    "src/Iron.App/Presentation/DeviceViewModels.cs": "b1c936436248e237f4f4f3f22d37735b9d4d42c77260739297aca2f2154fee7b"
   }
  },
  "rules": {
   "gpuTemperature": "GPU 코어 온도(NVML 'GPU Temperature'에 해당) >= 기준이면 해당 GPU만 즉시 경고. 핫스팟·메모리 정션은 Stage 1 기준이 없으므로 표시만 하고 경고하지 않음.",
   "pinLevel": "핀 전류 >= 기준이면 Over(적색), >= 기준 x pinApproachingShare 이면 Approaching(앰버)",
   "pinLatch": "Over 상태가 서로 다른 HWiNFO 타임스탬프 기준으로 pinLatchReadings회 연속이면 '핀 불균형 주의' 래치",
   "independence": "WireView 데이터가 없거나(USB 해제·미연결·INCOMPLETE) 해제되면 핀 경고만 해제되고 GPU 온도 경고는 영향받지 않음"
  },
  "notDefinedInStage1": [
   "imbalancePercent (RC2의 35%는 Stage 1에 없는 값)",
   "connectorTemperatureC (RC2의 75 °C는 Stage 1에 없는 값)",
   "pinCriticalA (RC2의 9.0 A는 Stage 1 입력 범위 상한이지 경고 기준이 아님)"
  ],
  "gpuTemperatureC": {
   "default": 72.0,
   "min": 55.0,
   "max": 95.0,
   "decimals": 0
  },
  "wireViewPinCurrentA": {
   "default": 7.0,
   "min": 4.0,
   "max": 9.0,
   "decimals": 1
  },
  "pinApproachingShare": 0.72,
  "pinLatchReadings": 3,
  "pinScaleMaxA": 9.5
 },
 "rules": {
  "version": 1,
  "_note": "HWiNFO read/sdb 리딩을 IRON 항목에 대응시키는 규칙. 이름(정규식)은 후보를 고르는 데만 쓰고, 채택은 SDB 'Unit'(정규화)이 허용 단위와 맞을 때만 한다. 실제 이름은 사용자 PC의 인벤토리(sdb_dump.json)로 확정해야 하며, 확정 전에는 후보·거부 사유를 모두 보고한다. 정규식은 JavaScript와 .NET에서 같은 뜻이 되는 문법만 사용한다.",
  "units": {
   "°C": [
    "°C",
    "℃",
    "C",
    "degC"
   ],
   "°F": [
    "°F",
    "F"
   ],
   "W": [
    "W"
   ],
   "A": [
    "A"
   ],
   "V": [
    "V"
   ],
   "%": [
    "%"
   ],
   "MHz": [
    "MHz"
   ],
   "MB": [
    "MB",
    "MiB"
   ],
   "GB": [
    "GB",
    "GiB"
   ],
   "RPM": [
    "RPM"
   ]
  },
  "groups": {
   "cpu": {
    "sensor": "^CPU \\[#(\\d+)\\]",
    "readings": {
     "tctl": {
      "name": "^(CPU \\()?Tctl/Tdie\\)?$",
      "units": [
       "°C"
      ]
     },
     "packagePower": {
      "name": "^(CPU Package Power|Package Power|CPU PPT)$",
      "units": [
       "W"
      ]
     },
     "usage": {
      "name": "^Total CPU Usage$",
      "units": [
       "%"
      ]
     }
    }
   },
   "gpu": {
    "sensor": "^GPU \\[#(\\d+)\\]",
    "readings": {
     "coreTemp": {
      "name": "^GPU Temperature$",
      "units": [
       "°C"
      ]
     },
     "hotspotTemp": {
      "name": "^GPU Hot ?Spot Temperature$|^GPU Hot ?Spot$",
      "units": [
       "°C"
      ]
     },
     "memJunctionTemp": {
      "name": "^GPU Memory Junction Temperature$",
      "units": [
       "°C"
      ]
     },
     "power": {
      "name": "^(GPU Power|Total Board Power|GPU Power \\(Total\\))$",
      "units": [
       "W"
      ]
     },
     "load": {
      "name": "^(GPU Core Load|GPU Utilization)$",
      "units": [
       "%"
      ]
     },
     "vramUsed": {
      "name": "^(GPU Memory Allocated|GPU Memory Used|GPU D3D Memory Dedicated)$",
      "units": [
       "MB",
       "GB"
      ]
     },
     "clock": {
      "name": "^GPU Clock$",
      "units": [
       "MHz"
      ]
     }
    }
   },
   "ram": {
    "sensor": "^System:",
    "readings": {
     "usedMB": {
      "name": "^Physical Memory Used$",
      "units": [
       "MB",
       "GB"
      ]
     },
     "availableMB": {
      "name": "^Physical Memory Available$",
      "units": [
       "MB",
       "GB"
      ]
     },
     "loadPct": {
      "name": "^Physical Memory Load$",
      "units": [
       "%"
      ]
     }
    }
   },
   "dimm": {
    "sensor": "DIMM|SPD|DDR5",
    "readings": {
     "temp": {
      "name": "Temperature|^DIMM",
      "units": [
       "°C"
      ]
     }
    }
   },
   "nvme": {
    "sensor": "S\\.M\\.A\\.R\\.T\\.|NVMe",
    "readings": {
     "temp": {
      "name": "^Drive Temperature( \\d+)?$|^Temperature$",
      "units": [
       "°C"
      ]
     },
     "remainingLife": {
      "name": "Remaining Life",
      "units": [
       "%"
      ]
     }
    }
   },
   "wireview": {
    "sensor": "WireView",
    "pinName": "(?:Pin|12V)\\s*#?\\s*([1-6])(?![0-9])",
    "pinUnits": {
     "currentA": [
      "A"
     ],
     "voltageV": [
      "V"
     ],
     "powerW": [
      "W"
     ]
    },
    "totalPower": {
     "name": "^(Total Power|Power Total|Connector Power)$",
     "units": [
      "W"
     ]
    },
    "temperature": {
     "name": "Temp",
     "units": [
      "°C"
     ]
    }
   }
  }
 },
 "gates": {
  "version": 1,
  "_note": "G1~G5 판정 기준의 단일 출처. poc/Collect-HWiNfoRest.ps1, poc/HWiNfoRestPoC (analyze), 문서가 모두 이 파일의 값을 사용한다. 단기 게이트 PASS는 LIVE_CAPTURE 데이터로만 가능하며, OFFLINE_JSON·SYNTHETIC 데이터로는 항상 UNVERIFIED.",
  "ports": {
   "basalt": 8080,
   "hwinfo": 8764,
   "relay": 8765,
   "forbidden": [
    8080
   ]
  },
  "hwinfo": {
   "expectedUpdateMs": 2000,
   "minPollMs": 500,
   "_source": "공식 HWiNFO_REST_API_SKILL.md (malikm/HWiNFO@97f5d5d): readings update at the sensor source's own rate (about every 2 s)"
  },
  "freshness": {
   "maxAgeMs": 6000,
   "maxFrozenMs": 6000,
   "_rule": "age = (수신 시각 + ping 시계 오프셋) - 'Timestamp ms'. 'Timestamp ms'가 maxFrozenMs 동안 증가하지 않거나 age > maxAgeMs 이면 STALE. RS <= Timestamp <= RE 위반 또는 역행은 INVALID(폐기)."
  },
  "G1": {
   "name": "API 연결·지연·신선도·오버헤드",
   "samples": 60,
   "intervalMs": 1000,
   "successRatePctMin": 100,
   "versionRequired": 1,
   "rttP95MsMax": 300,
   "readoutP95MsMax": 250,
   "timestampViolationsMax": 0,
   "staleSamplesMax": 0,
   "hwinfoCpuOverheadPctMax": 3.0,
   "_overhead": "REST 서버를 끈 기준선(Baseline 모드)과 REST 폴링 중(Collect 모드)의 HWiNFO64 CPU 사용률 차이. 기준선이 없으면 G1은 UNVERIFIED."
  },
  "G2": {
   "name": "SDB↔Read 매핑·단위 기반 센서 식별",
   "readToSdbMappingPctMin": 99.0,
   "unitMismatchesMax": 0,
   "decimalCommaSuspectsMax": 0,
   "expectedGpuCount": 2,
   "requiredCpu": [
    "tctl",
    "usage"
   ],
   "requiredPerGpu": [
    "coreTemp",
    "power",
    "load"
   ],
   "requiredRam": [
    "usedMB"
   ],
   "informational": [
    "cpu.packagePower",
    "gpu.hotspotTemp",
    "gpu.memJunctionTemp",
    "gpu.vramUsed",
    "dimm.temp",
    "nvme.temp",
    "nvme.remainingLife"
   ],
   "_decimalComma": "공식 문서는 Value 문자열의 쉼표를 지우라고만 한다. 소수점이 쉼표인 로캘이면 '64,8'이 648이 되므로, 쉼표 뒤 숫자가 1~2자리로 끝나는 값(천 단위 구분은 항상 3자리)을 의심 값으로 세어 1건이라도 있으면 G2 미달."
  },
  "G3A": {
   "name": "WireView Pro II 단독 접근 (HWiNFO만 접근)",
   "samples": 600,
   "intervalMs": 1000,
   "pinsRequired": 6,
   "frozenDistinctTimestampsMax": 10,
   "dropSamplesMax": 0,
   "requiresOperatorAttestation": true,
   "_rule": "6핀 전류(A) 모두 매 샘플에 존재해야 함(INCOMPLETE 0). WireView 센서의 모든 수치가 서로 다른 HWiNFO 타임스탬프 기준 frozenDistinctTimestampsMax회 넘게 같으면 FROZEN. 소유권은 REST 센서 노출만으로 확정하지 않으며 운영자 확인과 증거(프로세스·PnP)를 함께 기록한다."
  },
  "G3B": {
   "name": "WireView 동시 접근",
   "status": "NOT_RUN",
   "_rule": "사용자 별도 승인 전 실행 금지"
  },
  "G4S": {
   "name": "운용성 단기 확인 (수동 기록)",
   "checks": [
    "HWiNFO 재실행 후 REST 서버 자동 시작 여부 기록",
    "8764/8765 점유 시 기존 프로세스 무변경 중단(Exit 1)"
   ]
  },
  "G5": {
   "name": "Relay·Remote 자동 부정 테스트",
   "command": "python -m unittest discover -s tests -p \"test_*.py\" -v",
   "lanExposure": "NOT_RUN (--allow-lan 은 별도 승인 전 금지)"
  },
  "evidence": {
   "_note": "소유권·동시 접근 증거 수집용. 프로세스 이름은 추정이며 Inventory 결과로 사용자 PC의 실제 이름을 확인해 고쳐야 한다. 수집기는 어떤 프로세스도 종료하지 않는다.",
   "hwinfoProcessNames": [
    "HWiNFO64",
    "HWiNFO32",
    "HWiNFO"
   ],
   "wireViewClientProcessNames": [
    "WireView",
    "WireView2",
    "WireView2-SW",
    "WireViewPro2",
    "WireViewPro",
    "iUnity"
   ],
   "scanPattern": "wire|unity|iron|pccontrol|basalt|hwinfo",
   "usbIds": {
    "wireViewPro2": {
     "pnp": "VID_0483&PID_5740",
     "_source": "CLAUDE_CODE_IMPLEMENTATION_PROMPT.md (A-Slim) — STM32 USB CDC, EF05/EF06"
    },
    "antecFluxPro": {
     "pnp": "VID_2022&PID_0522",
     "_source": "CLAUDE_CODE_IMPLEMENTATION_PROMPT.md (A-Slim) — 12바이트 HID"
    }
   }
  },
  "exitCodes": {
   "0": "수집 완료 · G1 PASS (Inventory/Baseline은 정상 완료)",
   "1": "정책 위반·포트 상태 불일치 — 기존 프로세스 무변경 중단",
   "2": "연결·버전 실패",
   "3": "수집 완료 · G1 FAIL(기준 미달)",
   "4": "수집 완료 · G1 UNVERIFIED(기준선 없음·출처 미확인·합성)",
   "5": "WireView 동시 접근 위험 감지 — 수집 중단(프로세스는 건드리지 않음)"
  }
 }
}; })(typeof self !== 'undefined' ? self : this);
