param(
    [string]$MetricsJsonPath = "",
    [string]$OutputHtmlPath = ""
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "_resolve_paths.ps1")
$repoRoot = Resolve-TabRepoRoot
if ([string]::IsNullOrWhiteSpace($MetricsJsonPath)) {
    $MetricsJsonPath = Join-Path $repoRoot "reports\multifaction_m4_panorama_metrics.json"
}
if ([string]::IsNullOrWhiteSpace($OutputHtmlPath)) {
    $OutputHtmlPath = Join-Path $repoRoot "reports\multifaction_m4_panorama_report.html"
}

if (-not (Test-Path $MetricsJsonPath)) {
    throw "M4 metrics JSON not found: $MetricsJsonPath"
}

$rawJson = (Get-Content -Path $MetricsJsonPath -Raw -Encoding UTF8).Trim()

$html = @'
<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>《亿万僵尸》Phase 4 M4 正式多基地地图生成 &amp; 双出生模式全景检视报告</title>
  <style>
    :root {
      --bg: #0b0d11;
      --panel: #141820;
      --panel-2: #1a202b;
      --border: #2c3647;
      --brass: #d4a656;
      --brass-bright: #f0c975;
      --brass-dim: rgba(212, 166, 86, 0.16);
      --text: #e8edf5;
      --muted: #93a1b8;
      --green: #4cd48c;
      --red: #f26b6b;
      --cyan: #59c8f0;
      --amber: #f2a94d;
      --purple: #b58cff;
      --p1: #1e90ff;
      --p2: #00ff7f;
      --p3: #ff9f43;
    }
    * { box-sizing: border-box; }
    body {
      margin: 0;
      background: radial-gradient(circle at top center, #19202d 0%, var(--bg) 56%);
      color: var(--text);
      font-family: "Segoe UI", "Microsoft YaHei", "PingFang SC", sans-serif;
      line-height: 1.55;
    }
    .container { max-width: 1620px; margin: 0 auto; padding: 26px 24px 72px; }
    header.hero {
      background: linear-gradient(135deg, rgba(26,33,45,0.96), rgba(14,17,24,0.98));
      border: 1px solid rgba(212,166,86,0.4);
      border-radius: 14px;
      padding: 24px 30px;
      margin-bottom: 20px;
      box-shadow: 0 20px 46px rgba(0,0,0,0.48);
    }
    .hero-top {
      display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 12px; margin-bottom: 10px;
    }
    .hero-tag {
      display: inline-block; padding: 3px 11px; border-radius: 999px; font-size: 12px; font-weight: 700;
      letter-spacing: 0.05em; color: var(--brass-bright); background: var(--brass-dim);
      border: 1px solid rgba(212,166,86,0.42);
    }
    .nav-links { display: flex; flex-wrap: wrap; gap: 8px; }
    .nav-link {
      color: var(--cyan); text-decoration: none; font-size: 12px; font-weight: 600;
      padding: 5px 11px; border-radius: 8px; border: 1px solid rgba(89,200,240,0.3);
      background: rgba(89,200,240,0.08);
    }
    .nav-link:hover { background: rgba(89,200,240,0.18); }
    h1 { margin: 0 0 8px; font-size: 26px; font-weight: 700; color: #fff; }
    .hero-sub { color: var(--muted); margin: 0 0 18px; max-width: 1180px; font-size: 14px; }
    .pillars {
      display: grid; grid-template-columns: repeat(auto-fit, minmax(230px, 1fr)); gap: 10px; margin-bottom: 18px;
    }
    .pillar {
      background: rgba(11,13,17,0.65); border: 1px solid var(--border); border-radius: 10px;
      padding: 10px 13px; font-size: 12.5px; color: var(--muted);
    }
    .pillar strong { color: var(--brass-bright); display: block; margin-bottom: 2px; font-size: 13px; }
    .kpi-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(175px, 1fr)); gap: 11px; }
    .kpi {
      background: var(--panel-2); border: 1px solid var(--border); border-radius: 10px; padding: 11px 14px;
    }
    .kpi .label { font-size: 11.5px; color: var(--muted); }
    .kpi .value { font-size: 20px; font-weight: 700; color: var(--brass-bright); margin-top: 2px; }
    .kpi .value.ok { color: var(--green); }
    .kpi .value.warn { color: var(--amber); }
    .kpi .sub { font-size: 11px; color: var(--muted); margin-top: 2px; }

    .controls-bar {
      position: sticky; top: 8px; z-index: 30;
      display: flex; flex-wrap: wrap; gap: 10px; align-items: center; justify-content: space-between;
      padding: 11px 15px; margin-bottom: 18px;
      background: rgba(20, 24, 32, 0.94); border: 1px solid rgba(212,166,86,0.28);
      border-radius: 12px; backdrop-filter: blur(8px); box-shadow: 0 10px 26px rgba(0,0,0,0.4);
    }
    .tab-group { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
    .tab-btn {
      border: 1px solid var(--border); background: var(--panel-2); color: var(--text);
      padding: 5px 11px; border-radius: 8px; cursor: pointer; font-size: 12.5px; font-weight: 600; transition: 0.15s;
    }
    .tab-btn:hover { border-color: var(--brass); }
    .tab-btn.active { background: var(--brass); color: #111317; border-color: var(--brass-bright); }
    .tab-btn.toggle-on { background: rgba(76,212,140,0.14); border-color: rgba(76,212,140,0.5); color: var(--green); }

    .legend { display: flex; flex-wrap: wrap; gap: 14px; font-size: 12px; color: var(--muted); margin-bottom: 16px; padding: 0 4px; }
    .legend span { display: inline-flex; align-items: center; gap: 6px; }
    .dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }

    .scenario-card {
      background: var(--panel); border: 1px solid var(--border); border-radius: 14px;
      padding: 20px 22px; margin-bottom: 24px; box-shadow: 0 12px 30px rgba(0,0,0,0.34);
    }
    .sc-head {
      display: flex; flex-wrap: wrap; justify-content: space-between; align-items: flex-start; gap: 12px; margin-bottom: 10px;
    }
    .sc-title { font-size: 19px; font-weight: 700; margin: 0; color: #fff; }
    .sc-meta { font-size: 12.5px; color: var(--muted); }

    .pano-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 14px; margin-bottom: 16px; }
    @media (max-width: 1080px) { .pano-grid { grid-template-columns: 1fr; } }

    .pano-box {
      background: #0d1016; border: 1px solid var(--border); border-radius: 11px; overflow: hidden;
      display: flex; flex-direction: column;
    }
    .pano-bar {
      display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 6px;
      padding: 8px 12px; background: var(--panel-2); border-bottom: 1px solid var(--border); font-size: 12.5px;
    }
    .pills-row { display: flex; gap: 6px; align-items: center; flex-wrap: wrap; }
    .pill {
      display: inline-block; padding: 2px 8px; border-radius: 6px; font-size: 11px; font-weight: 700;
    }
    .pill-2p { background: rgba(89,200,240,0.16); color: var(--cyan); border: 1px solid rgba(89,200,240,0.4); }
    .pill-3p { background: rgba(212,166,86,0.2); color: var(--brass-bright); border: 1px solid rgba(212,166,86,0.45); }
    .pill-sep { background: rgba(76,212,140,0.14); color: var(--green); border: 1px solid rgba(76,212,140,0.4); }
    .pill-cls { background: rgba(181,140,255,0.16); color: var(--purple); border: 1px solid rgba(181,140,255,0.45); }
    .pill-tend { background: rgba(242,169,77,0.16); color: var(--amber); border: 1px solid rgba(242,169,77,0.4); }

    .pano-img-wrap {
      position: relative; aspect-ratio: 1 / 1; background: #07090d; overflow: hidden; cursor: zoom-in;
    }
    .pano-img-wrap img { width: 100%; height: 100%; object-fit: cover; display: block; transition: transform 0.25s ease; }
    .pano-img-wrap:hover img { transform: scale(1.012); }

    .cc-overlay { position: absolute; inset: 0; pointer-events: none; transition: opacity 0.2s; }
    .cc-overlay.hidden { opacity: 0; }
    .cc-overlay svg.overlay-lines { position: absolute; inset: 0; width: 100%; height: 100%; }

    .cc-pin { position: absolute; transform: translate(-50%, -50%); }
    .cc-pin .ring-safe {
      position: absolute; left: 50%; top: 50%; transform: translate(-50%, -50%);
      border-radius: 50%; border: 1.5px dashed rgba(255,255,255,0.68); background: rgba(255,255,255,0.03);
    }
    .cc-pin .core {
      width: 14px; height: 14px; border-radius: 50%; border: 2px solid #fff;
      box-shadow: 0 0 10px rgba(0,0,0,0.95);
    }
    .cc-pin .lbl {
      position: absolute; left: 10px; top: -11px; white-space: nowrap;
      font-size: 11px; font-weight: 700; padding: 1px 6px; border-radius: 4px;
      background: rgba(10,12,17,0.88); color: #fff; border: 1px solid rgba(255,255,255,0.28);
    }

    .zoom-hint {
      position: absolute; right: 10px; bottom: 10px; padding: 3px 8px; border-radius: 6px;
      background: rgba(0,0,0,0.72); border: 1px solid rgba(255,255,255,0.18);
      font-size: 11px; color: #ddd; pointer-events: none;
    }

    .tier-bar-wrap { padding: 8px 12px 6px; background: #10141c; border-top: 1px solid var(--border); }
    .tier-bar-label {
      display: flex; justify-content: space-between; font-size: 11px; color: var(--muted); margin-bottom: 4px;
    }
    .tier-bar {
      height: 8px; border-radius: 999px; overflow: hidden; display: flex; background: #1c222e;
    }
    .seg-w { background: #5a677d; }
    .seg-m { background: #4b8bbe; }
    .seg-s { background: #f0c975; }
    .seg-c { background: #e87a4a; }
    .seg-v { background: #5bd678; }
    .seg-h { background: #c57bff; }

    .pano-foot {
      padding: 7px 12px 9px; font-size: 12px; color: var(--muted);
      display: flex; flex-wrap: wrap; gap: 10px; justify-content: space-between;
      border-top: 1px solid rgba(44,54,71,0.5); background: #11151d;
    }
    .pano-foot strong { color: var(--text); }

    table.metrics { width: 100%; border-collapse: collapse; font-size: 12.5px; overflow-x: auto; display: block; }
    table.metrics th, table.metrics td {
      padding: 7px 9px; border-bottom: 1px solid var(--border); text-align: left; white-space: nowrap;
    }
    table.metrics th { color: var(--brass-bright); font-weight: 600; background: #181d27; font-size: 11.5px; }
    table.metrics tr:hover td { background: rgba(255,255,255,0.025); }
    .badge {
      display: inline-block; padding: 1px 6px; border-radius: 5px; font-size: 11px; font-weight: 700; margin-left: 4px;
    }
    .b-ok { background: rgba(76,212,140,0.16); color: var(--green); }
    .b-warn { background: rgba(242,169,77,0.16); color: var(--amber); }
    .b-bad { background: rgba(242,107,107,0.16); color: var(--red); }

    .lightbox {
      display: none; position: fixed; inset: 0; z-index: 100; background: rgba(4, 6, 9, 0.94);
      align-items: center; justify-content: center; padding: 24px;
    }
    .lightbox.open { display: flex; }
    .lightbox-inner { position: relative; max-width: min(92vh, 92vw); max-height: 92vh; }
    .lightbox-stage {
      position: relative; width: min(86vh, 86vw); height: min(86vh, 86vw);
      border-radius: 10px; overflow: hidden; border: 1px solid rgba(212,166,86,0.55);
    }
    .lightbox-stage img { width: 100%; height: 100%; object-fit: contain; display: block; background: #000; }
    .lightbox-caption { text-align: center; margin-top: 10px; color: var(--brass-bright); font-size: 13.5px; }
    .lightbox-close {
      position: absolute; top: -14px; right: -14px; width: 34px; height: 34px; border-radius: 50%;
      background: var(--brass); color: #111; border: none; font-size: 18px; font-weight: 700; cursor: pointer; z-index: 2;
    }
  </style>
</head>
<body>
<div class="container">
  <header class="hero">
    <div class="hero-top">
      <span class="hero-tag">PHASE 4 · M4 FORMAL MULTI-BASE GENERATOR · ENGINE VERIFIED</span>
      <div class="nav-links">
        <a class="nav-link" href="multifaction_panorama_report.html">↩ 对比 M1 原型基线报告 (256/304 格)</a>
      </div>
    </div>
    <h1>《亿万僵尸》独立基地（M4）全 6 生态地图生成 × 双出生模式全景检视</h1>
    <p class="hero-sub">
      本报告由 M4 正式多基地生成器（<code>MultiBaseMapGenerator</code>）与多方向尸潮系统（<code>MultiWaveSwarmPatches</code>）在真实游戏引擎内批量生成与实测采样（24 张 2048×2048 实机全景图 + 运行时实体与路径连通统计），对照检视 <strong>Separated（对称分离）</strong> 与 <strong>Clustered（紧密对脸）</strong> 在 2P（360×360）与 3P（416×416）下的地形、资源、3 环僵尸升格与多股尸潮分流。
    </p>

    <div class="pillars">
      <div class="pillar"><strong>1. 地图扩容 &amp; 去伪就真</strong>2P 固定 360×360（Area 1.98×）、3P 固定 416×416（Area 2.64×），非破坏性迁移 CC1 + Top-K 自然打分 + 全局矿保底。</div>
      <div class="pillar"><strong>2. Separated vs Clustered</strong>分离模式对称分散在 ~80-105 格并自动开凿主干通廊；聚集模式面对面 18-24 格开局电网重叠但内圈狭窄。</div>
      <div class="pillar"><strong>3. 3 环折算 + 种子倾向</strong>0-28 格绝对安全；28-48 保留 T1；48-75 半量 4:1 升格中坚；&ge;75 按 <code>Balanced/HeavyArmor/Caustic/SwarmHunter</code> 浓缩特感与巨人/变异体。</div>
      <div class="pillar"><strong>4. N 股分流 + 后期降卡</strong>W1-2 100% 独立外侧出兵点；W3+ 75% 分散 / 20% 两股合流 / 5% 三股；&ge;55d 与终波 850/1400 上限折算精英与变异体。</div>
    </div>

    <div class="kpi-grid" id="kpiGrid"></div>
  </header>

  <div class="controls-bar">
    <div class="tab-group" id="themeTabs"></div>
    <div class="tab-group">
      <button class="tab-btn active" data-pfilter="all">全部人数</button>
      <button class="tab-btn" data-pfilter="2">2P (360格)</button>
      <button class="tab-btn" data-pfilter="3">3P (416格)</button>
      <span style="color:var(--border)">|</span>
      <button class="tab-btn active" data-sfilter="all">全部出生</button>
      <button class="tab-btn" data-sfilter="Separated">Separated 分离</button>
      <button class="tab-btn" data-sfilter="Clustered">Clustered 聚集</button>
      <button class="tab-btn toggle-on" id="toggleMarkersBtn">◎ 显示基地与 R=28 安全环</button>
    </div>
  </div>

  <div class="legend">
    <span><i class="dot" style="background:var(--p1)"></i>P1 (Blue)</span>
    <span><i class="dot" style="background:var(--p2)"></i>P2 (Green)</span>
    <span><i class="dot" style="background:var(--p3)"></i>P3 (Orange)</span>
    <span><i class="dot" style="border:1px dashed #fff"></i>虚线圆：R=28 绝对安全圈</span>
    <span><i class="dot" style="background:#5a677d"></i>Weak</span>
    <span><i class="dot" style="background:#4b8bbe"></i>Med/Runner</span>
    <span><i class="dot" style="background:#f0c975"></i>Strong</span>
    <span><i class="dot" style="background:#e87a4a"></i>Chubby</span>
    <span><i class="dot" style="background:#5bd678"></i>Venom</span>
    <span><i class="dot" style="background:#c57bff"></i>Harpy</span>
  </div>

  <div id="app"></div>
</div>

<div class="lightbox" id="lightbox">
  <div class="lightbox-inner">
    <button class="lightbox-close" id="lightboxClose">×</button>
    <div class="lightbox-stage">
      <img id="lightboxImg" src="" alt="M4 Panorama Zoom" />
      <div class="cc-overlay" id="lbOverlay"></div>
    </div>
    <div class="lightbox-caption" id="lightboxCaption"></div>
  </div>
</div>

<script>
const DATA = __JSON_PAYLOAD__;

const THEME_META = {
  BR: { cnTitle: "暗黑荒原 · The Dark Moorland (BR)", tag: "标准均衡温带地貌" },
  AL: { cnTitle: "宁静低地 · The Peaceful Lowlands (AL)", tag: "富草肥沃林间地貌" },
  TM: { cnTitle: "荒芜旷野 · The Frozen Highlands (TM)", tag: "峭壁分隔高难度隘口" },
  DS: { cnTitle: "干旱高地 · The Desolated Wasteland (DS)", tag: "高噪低肥缺草缺水" },
  FA: { cnTitle: "深寒苔原 · The Frozen Lake / Tundra (FA)", tag: "寒冷冰湖降产" },
  VO: { cnTitle: "酸蚀之地 · The Caustic Lands (VO)", tag: "高突变剧毒地貌" }
};

let activeTheme = "ALL";
let activePFilter = "all";
let activeSFilter = "all";
let showMarkers = true;

function fmt(n) { return (n ?? 0).toLocaleString("en-US"); }

function renderKpis() {
  const all = DATA.flatMap(t => t.cases);
  const totalMaps = all.length;
  const connectedCount = all.filter(c => c.metrics.allCcsConnected === true).length;
  let minZ = 9999;
  let unsafeCount = 0;
  let minStoneSumSep = 9999;
  let w1Pass = 0, w2Pass = 0;
  const tendencies = {};

  all.forEach(c => {
    const m = c.metrics;
    const sw = c.swarmAudit || {};
    if (sw.wave1Distinct) w1Pass++;
    if (sw.wave2Distinct) w2Pass++;
    tendencies[m.mutationTendency] = (tendencies[m.mutationTendency] || 0) + 1;

    let stoneSum = 0;
    let caseMinZ = 9999;
    (m.commandCenters || []).forEach(cc => {
      if (cc.nearestZombieDist >= 0 && cc.nearestZombieDist < caseMinZ) caseMinZ = cc.nearestZombieDist;
      stoneSum += (cc.stone24 || 0);
    });
    if (caseMinZ < minZ) minZ = caseMinZ;
    if (caseMinZ < 27.5) unsafeCount++;
    if (c.spawnMode === "Separated" && stoneSum < minStoneSumSep) minStoneSumSep = stoneSum;
  });

  const tendDesc = Object.entries(tendencies).map(([k, v]) => `${k}:${v}`).join(" · ");

  document.getElementById("kpiGrid").innerHTML = `
    <div class="kpi"><div class="label">M4 实机全景样本</div><div class="value">${totalMaps} 局</div><div class="sub">6 生态 × (2P/3P × 分离/聚集)</div></div>
    <div class="kpi"><div class="label">全基地步兵连通 (BFS)</div><div class="value ${connectedCount === totalMaps ? "ok" : "warn"}">${connectedCount}/${totalMaps}</div><div class="sub">含隐蔽通道 A* 重开凿</div></div>
    <div class="kpi"><div class="label">R&lt;28 危险刷怪事件</div><div class="value ${unsafeCount === 0 ? "ok" : "warn"}">${unsafeCount}/${totalMaps}</div><div class="sub">实测最近僵尸 ${minZ.toFixed(1)} 格</div></div>
    <div class="kpi"><div class="label">分离模式团队石矿保底</div><div class="value ok">&ge; ${minStoneSumSep} 格</div><div class="sub">目标团队总和 &ge; 4 格</div></div>
    <div class="kpi"><div class="label">W1 / W2 独立多向分流</div><div class="value ok">${w1Pass}/${totalMaps} · ${w2Pass}/${totalMaps}</div><div class="sub">Wave 3+ 自动含合流概率</div></div>
    <div class="kpi"><div class="label">种子突变倾向分布</div><div class="value" style="font-size:15px;margin-top:6px;">${tendDesc}</div><div class="sub">外环特感/巨人/变异体联动</div></div>
  `;
}

function renderTabs() {
  const tabsEl = document.getElementById("themeTabs");
  const btns = [{ key: "ALL", label: "全部 6 个生态 (24张)" }, ...DATA.map(t => ({ key: t.theme, label: t.cnName }))];
  tabsEl.innerHTML = btns.map(b =>
    `<button class="tab-btn ${activeTheme === b.key ? "active" : ""}" data-theme="${b.key}">${b.label}</button>`
  ).join("");
  tabsEl.querySelectorAll("button").forEach(btn => {
    btn.addEventListener("click", () => {
      activeTheme = btn.dataset.theme;
      renderTabs();
      renderMain();
    });
  });
}

function ccPinCoords(cc, m) {
  const nCells = m.gridNCells || 512;
  const playableW = m.playableAreaWidth || 256;
  const sceneDx = (cc.x - cc.y) * 0.70710677;
  const sceneDy = (cc.x + cc.y - nCells) * 0.5;
  const px = 50 + (sceneDx / playableW) * 100;
  const py = 50 + (sceneDy / playableW) * 100;
  return {
    x: Math.max(2, Math.min(98, px)),
    y: Math.max(2, Math.min(98, py))
  };
}

function renderOverlayHtml(c) {
  const m = c.metrics;
  const playableW = m.playableAreaWidth || 256;
  const ccs = m.commandCenters || [];

  // Isometric R=28 safe ring projection in 0..100 SVG viewBox coordinates
  const ringRx = ((28 * 0.70710677) / playableW) * 100;
  const ringRy = ((28 * 0.5) / playableW) * 100;

  const svgElems = [];
  ccs.forEach(cc => {
    const pos = ccPinCoords(cc, m);
    const col = cc.index === 1 ? "#1e90ff" : (cc.index === 2 ? "#00ff7f" : "#ff9f43");
    svgElems.push(`<ellipse cx="${pos.x.toFixed(2)}" cy="${pos.y.toFixed(2)}" rx="${ringRx.toFixed(2)}" ry="${ringRy.toFixed(2)}" fill="rgba(255,255,255,0.03)" stroke="${col}" stroke-opacity="0.72" stroke-width="0.4" stroke-dasharray="1.2 0.8" />`);
  });
  if (ccs.length >= 2) {
    for (let i = 0; i < ccs.length; i++) {
      for (let j = i + 1; j < ccs.length; j++) {
        const a = ccs[i], b = ccs[j];
        const pa = ccPinCoords(a, m), pb = ccPinCoords(b, m);
        const dist = Math.hypot(a.x - b.x, a.y - b.y).toFixed(0);
        const mx = (pa.x + pb.x) / 2, my = (pa.y + pb.y) / 2;
        svgElems.push(`<line x1="${pa.x.toFixed(2)}" y1="${pa.y.toFixed(2)}" x2="${pb.x.toFixed(2)}" y2="${pb.y.toFixed(2)}" stroke="rgba(240,201,117,0.82)" stroke-width="0.45" stroke-dasharray="1.1 0.8" />`);
        svgElems.push(`<rect x="${(mx - 4.4).toFixed(2)}" y="${(my - 2.3).toFixed(2)}" width="8.8" height="4.6" rx="1.2" fill="rgba(10,12,17,0.86)" stroke="rgba(240,201,117,0.65)" stroke-width="0.25"/>`);
        svgElems.push(`<text x="${mx.toFixed(2)}" y="${(my + 0.9).toFixed(2)}" text-anchor="middle" fill="#f0c975" font-size="2.8" font-weight="700">${dist}g</text>`);
      }
    }
  }
  const linesSvg = `<svg class="overlay-lines" viewBox="0 0 100 100" preserveAspectRatio="none">${svgElems.join("")}</svg>`;

  const pins = ccs.map(cc => {
    const pos = ccPinCoords(cc, m);
    const col = cc.index === 1 ? "var(--p1)" : (cc.index === 2 ? "var(--p2)" : "var(--p3)");
    return `
      <div class="cc-pin" style="left:${pos.x.toFixed(2)}%;top:${pos.y.toFixed(2)}%;">
        <div class="core" style="background:${col}"></div>
        <div class="lbl" style="border-color:${col}">P${cc.index} (${cc.x},${cc.y})</div>
      </div>
    `;
  }).join("");

  return linesSvg + pins;
}

function renderTierBar(m) {
  const w = m.weakZombies || 0, med = m.mediumZombies || 0, str = m.strongZombies || 0;
  const c = m.chubbyZombies || 0, v = m.venomZombies || 0, h = m.harpyZombies || 0;
  const tot = Math.max(1, w + med + str + c + v + h);
  const pct = (x) => ((x / tot) * 100).toFixed(1) + "%";
  return `
    <div class="tier-bar-wrap">
      <div class="tier-bar-label">
        <span>3环僵尸构成: 弱${fmt(w)} / 中${fmt(med)} / 强${fmt(str)} | <strong>特感 C${fmt(c)}·V${fmt(v)}·H${fmt(h)}</strong></span>
        <span>Boss: G=${m.giants} · M=${m.mutants}</span>
      </div>
      <div class="tier-bar">
        <div class="seg-w" style="width:${pct(w)}" title="Weak: ${w}"></div>
        <div class="seg-m" style="width:${pct(med)}" title="Medium: ${med}"></div>
        <div class="seg-s" style="width:${pct(str)}" title="Strong: ${str}"></div>
        <div class="seg-c" style="width:${pct(c)}" title="Chubby: ${c}"></div>
        <div class="seg-v" style="width:${pct(v)}" title="Venom: ${v}"></div>
        <div class="seg-h" style="width:${pct(h)}" title="Harpy: ${h}"></div>
      </div>
    </div>
  `;
}

function renderCaseTable(cases) {
  const rows = [];
  cases.forEach(c => {
    const m = c.metrics;
    const sw = c.swarmAudit || {};
    (m.commandCenters || []).forEach((cc, idx) => {
      const col = cc.index === 1 ? "var(--p1)" : (cc.index === 2 ? "var(--p2)" : "var(--p3)");
      const tag = idx === 0
        ? `<span class="pill ${c.playerCount >= 3 ? "pill-3p" : "pill-2p"}">${c.playerCount}P</span> <span class="pill ${c.spawnMode === "Clustered" ? "pill-cls" : "pill-sep"}">${c.spawnMode}</span>`
        : `<span style="color:var(--muted);font-size:11.5px;">↳ 队友基地 ${cc.index}</span>`;
      const zBadge = cc.nearestZombieDist >= 28 ? `<span class="badge b-ok">安全</span>` : `<span class="badge b-bad">险</span>`;
      const dDist = (cc.nearestDoomDist !== undefined && cc.nearestDoomDist >= 0) ? `${cc.nearestDoomDist} 格` : "—";
      const dBadge = (cc.nearestDoomDist !== undefined && cc.nearestDoomDist >= 0)
        ? (cc.nearestDoomDist >= 32 ? `<span class="badge b-ok">安全</span>` : `<span class="badge b-bad">险</span>`)
        : "";
      const connBadge = m.allCcsConnected ? `<span class="badge b-ok">连通</span>` : `<span class="badge b-bad">隔断</span>`;
      const swInfo = idx === 0 ? `W1:${sw.wave1Edges || "-"} | 合并×${sw.mergedWavesAcross23LateTrials ?? 0}/23` : "—";
      rows.push(`
        <tr>
          <td>${tag}</td>
          <td><span class="dot" style="background:${col}"></span> <strong>CC${cc.index}</strong> (${cc.x}, ${cc.y})</td>
          <td>${cc.minOtherCcDist} 格</td>
          <td>${fmt(cc.trees16)}</td>
          <td>${fmt(cc.stone24)}</td>
          <td>${fmt(cc.iron48)}</td>
          <td>${fmt(cc.oil48)}</td>
          <td>${cc.nearestZombieDist} 格${zBadge}</td>
          <td>${dDist}${dBadge}</td>
          <td>${idx === 0 ? `${m.mutationTendency}${connBadge}` : "—"}</td>
          <td>${swInfo}</td>
        </tr>
      `);
    });
  });

  return `
    <table class="metrics">
      <thead>
        <tr>
          <th>模式配置</th>
          <th>基地 (坐标)</th>
          <th>最近友军距</th>
          <th>木材 r&le;16</th>
          <th>石矿 r&le;24</th>
          <th>铁矿 r&le;48</th>
          <th>油井 r&le;48</th>
          <th>最近僵尸</th>
          <th>最近巢穴</th>
          <th>种子倾向 / 连通</th>
          <th>N 股尸潮审计 (25 波推演)</th>
        </tr>
      </thead>
      <tbody>${rows.join("")}</tbody>
    </table>
  `;
}

function renderMain() {
  const app = document.getElementById("app");
  const themes = activeTheme === "ALL" ? DATA : DATA.filter(t => t.theme === activeTheme);

  const htmlBlocks = themes.map(th => {
    const meta = THEME_META[th.theme] || { cnTitle: th.cnName, tag: th.theme };
    const filtered = th.cases.filter(c => {
      if (activePFilter !== "all" && String(c.playerCount) !== activePFilter) return false;
      if (activeSFilter !== "all" && c.spawnMode !== activeSFilter) return false;
      return true;
    });
    if (filtered.length === 0) return "";

    const cards = filtered.map(c => {
      const m = c.metrics;
      const sw = c.swarmAudit || {};
      const caption = `${meta.cnTitle} · Seed ${c.seed} · ${c.playerCount}P ${c.spawnMode} (${m.survivalNCells}格) · ${m.mutationTendency}`;
      return `
        <div class="pano-box">
          <div class="pano-bar">
            <span>Seed <code>${c.seed}</code> · CC1距心 <code>${m.cc1DistFromCenter}g</code></span>
            <div class="pills-row">
              <span class="pill ${c.playerCount >= 3 ? "pill-3p" : "pill-2p"}">${c.playerCount}P ${m.survivalNCells}格</span>
              <span class="pill ${c.spawnMode === "Clustered" ? "pill-cls" : "pill-sep"}">${c.spawnMode}</span>
              <span class="pill pill-tend">${m.mutationTendency}</span>
            </div>
          </div>
          <div class="pano-img-wrap" data-case="${c.id}" data-caption="${caption}">
            <img src="${c.image}" loading="lazy" alt="${c.id}" />
            <div class="cc-overlay ${showMarkers ? "" : "hidden"}">${renderOverlayHtml(c)}</div>
            <span class="zoom-hint">点击放大 2048×2048</span>
          </div>
          ${renderTierBar(m)}
          <div class="pano-foot">
            <span>总感染: <strong>${fmt(m.zombiesTotal)}</strong></span>
            <span>巢穴实体: <strong>${m.doomVillages}</strong></span>
            <span>游侠/士兵: <strong>${m.rangers}/${m.soldiers}</strong></span>
            <span>W1: <strong>${sw.wave1Edges || "-"}</strong> · 连通: <strong style="color:${m.allCcsConnected ? "var(--green)" : "var(--red)"}">${m.allCcsConnected ? "YES" : "NO"}</strong></span>
          </div>
        </div>
      `;
    }).join("");

    return `
      <section class="scenario-card">
        <div class="sc-head">
          <div>
            <h2 class="sc-title">${meta.cnTitle}</h2>
            <div class="sc-meta">${meta.tag} · 同种子 (<code>${th.seed}</code>) 下 2P/3P × 分离/聚集 全景对比</div>
          </div>
        </div>
        <div class="pano-grid">${cards}</div>
        ${renderCaseTable(filtered)}
      </section>
    `;
  }).join("");

  app.innerHTML = htmlBlocks || `<div class="scenario-card">当前筛选条件下无匹配样本，请放宽上方筛选按钮。</div>`;

  const byId = {};
  DATA.forEach(th => th.cases.forEach(c => { byId[c.id] = c; }));
  document.querySelectorAll(".pano-img-wrap[data-case]").forEach(el => {
    el.addEventListener("click", () => {
      const c = byId[el.dataset.case];
      document.getElementById("lightboxImg").src = c.image;
      const ov = document.getElementById("lbOverlay");
      ov.innerHTML = renderOverlayHtml(c);
      ov.classList.toggle("hidden", !showMarkers);
      document.getElementById("lightboxCaption").textContent = el.dataset.caption + " (2048×2048)";
      document.getElementById("lightbox").classList.add("open");
    });
  });
}

document.querySelectorAll("button[data-pfilter]").forEach(btn => {
  btn.addEventListener("click", () => {
    document.querySelectorAll("button[data-pfilter]").forEach(b => b.classList.remove("active"));
    btn.classList.add("active");
    activePFilter = btn.dataset.pfilter;
    renderMain();
  });
});

document.querySelectorAll("button[data-sfilter]").forEach(btn => {
  btn.addEventListener("click", () => {
    document.querySelectorAll("button[data-sfilter]").forEach(b => b.classList.remove("active"));
    btn.classList.add("active");
    activeSFilter = btn.dataset.sfilter;
    renderMain();
  });
});

const toggleBtn = document.getElementById("toggleMarkersBtn");
toggleBtn.addEventListener("click", () => {
  showMarkers = !showMarkers;
  toggleBtn.classList.toggle("toggle-on", showMarkers);
  toggleBtn.textContent = showMarkers ? "◎ 显示基地与 R=28 安全环" : "○ 已隐藏基地标靶圈";
  document.querySelectorAll(".cc-overlay").forEach(ov => ov.classList.toggle("hidden", !showMarkers));
});

const lb = document.getElementById("lightbox");
document.getElementById("lightboxClose").addEventListener("click", () => lb.classList.remove("open"));
lb.addEventListener("click", (e) => { if (e.target === lb) lb.classList.remove("open"); });
window.addEventListener("keydown", (e) => { if (e.key === "Escape") lb.classList.remove("open"); });

renderKpis();
renderTabs();
renderMain();
</script>
</body>
</html>
'@

$finalHtml = $html.Replace("__JSON_PAYLOAD__", $rawJson)
Set-Content -Path $OutputHtmlPath -Value $finalHtml -Encoding UTF8
Write-Host "Generated M4 showcase HTML report: $OutputHtmlPath ($([Math]::Round((Get-Item $OutputHtmlPath).Length / 1024.0, 1)) KB)"
