// Vidéo « Miniatures au survol » — LinkedIn 4:5 (1080×1350).
// Revit seul (des noms et des « ? ») → balayage vert → miniatures dans l'arborescence → rideau « Et sur les onglets » → onglets → fin.
// node gen.mjs  → écrit index.html et compositions/reel.html
import { writeFileSync } from "node:fs";

const TREE_LEN = 7.13, TABS_LEN = 6.86;           // durées des clips préparés (assets/tree.mp4, tabs.mp4)
const SWEEP = 3.0, W = 0.35;                      // balayage Revit → BIMaestro
const TREE_END = SWEEP + TREE_LEN;                // fin du plan arborescence
const CURTAIN = TREE_END - 0.4;                   // le rideau monte
const TABS = CURTAIN + 1.2;                       // plan onglets
const REEL = +(TABS + TABS_LEN).toFixed(2);
const OUTRO = 2.8, TOTAL = +(REEL + OUTRO).toFixed(2);

// « ? » à droite des noms de vues qui se ressemblent (coordonnées de l'image source 900×960)
const qs = [[318, 238], [318, 263], [346, 363], [384, 388]];
const qHtml = qs.map(([x, y], i) => `      <div id="q${i}" class="q" style="left:${x + 4}px;top:${y - 17}px">?</div>`).join("\n");
const qTw = qs.map((_, i) => `tl.fromTo("#q${i}", { scale: 0, rotation: -25 }, { scale: 1, rotation: 0, duration: 0.35, ease: "back.out(3)" }, ${(0.5 + i * 0.22).toFixed(2)});`).join("\n    ");

const subs = [
  ["s1", 0, "Laquelle est <b>la bonne vue ?</b>"],
  ["s2", SWEEP + 0.25, "Survole. <b>Tu la vois.</b>"],
  ["s3", SWEEP + 3.8, "Avec la date, <b>à jour ou pas.</b>"],
  ["s4", TABS + 0.3, "La vue <b>avant de l'ouvrir.</b>"],
];
const subEnd = (i) => (i + 1 < subs.length ? subs[i + 1][1] - 0.25 : REEL - 0.35);
const subTw = subs.map(([id, t], i) => [
  i === 0 ? `tl.fromTo("#${id}", { opacity: 0, y: 24 }, { opacity: 1, y: 0, duration: 0.35, ease: "expo.out" }, 0.15);`
          : `tl.fromTo("#${id}", { opacity: 0, y: 30 }, { opacity: 1, y: 0, duration: 0.3, ease: "expo.out" }, ${t.toFixed(2)});`,
  i === subs.length - 1 ? "" : `tl.to("#${id}", { opacity: 0, y: -20, duration: 0.2 }, ${subEnd(i).toFixed(2)});`,
].join("\n    ")).join("\n    ");

const reel = `<template>
  <style>
    #root { position: absolute; inset: 0; }
    .card { position: absolute; border-radius: 26px; overflow: hidden; background: #fff; box-shadow: 0 30px 80px rgba(17,24,39,.18), 0 0 0 2px #E5E7EB; }
    #tree { left: 90px; top: 170px; width: 900px; height: 960px; }
    #tabs { left: 60px; top: 330px; width: 960px; height: 413px; }
    #tree.on, #tabs { box-shadow: 0 30px 80px rgba(17,24,39,.22), 0 0 0 6px #0F5132; }
    .media { position: absolute; left: 0; top: 0; width: 100%; height: 100%; object-fit: fill; }
    #treev { z-index: 2; clip-path: inset(0% 0% 100% 0%); }
    #line { position: absolute; left: 0; right: 0; top: 0; height: 8px; background: #0F5132; box-shadow: 0 0 22px #7FD6A6; z-index: 60; opacity: 0; }
    .q { position: absolute; z-index: 5; width: 34px; height: 34px; border-radius: 50%; background: #6B7280; color: #fff; font-weight: 900; font-size: 24px; display: flex; align-items: center; justify-content: center; box-shadow: 0 4px 12px rgba(17,24,39,.25); }
    .pill { position: absolute; top: 70px; left: 0; right: 0; text-align: center; }
    .pill span { display: inline-block; font-weight: 900; font-size: 38px; padding: 10px 26px; border-radius: 999px; }
    #p1 span { background: #E5E7EB; color: #374151; }
    #p2 span { background: #0F5132; color: #fff; }
    #p2 { opacity: 0; }
    .sub { position: absolute; top: 1185px; left: 50px; right: 50px; text-align: center; font-weight: 900; font-size: 60px; letter-spacing: -0.03em; color: #111827; opacity: 0; }
    .sub b { color: #0F5132; }
    #s4 { top: 820px; }
    #curtain { position: absolute; inset: 0; background: #0F5132; z-index: 90; display: flex; align-items: center; justify-content: center; color: #fff; font-weight: 900; font-size: 84px; letter-spacing: -0.04em; }
  </style>
  <div id="root" data-composition-id="reel" data-width="1080" data-height="1350">
    <div id="p1" class="pill"><span>Revit</span></div>
    <div id="p2" class="pill"><span>Revit + BIMaestro</span></div>
    <div id="tree" class="card">
      <img class="media" src="assets/tree0.png" alt="" />
      <video id="treev" class="clip media" src="assets/tree.mp4" data-start="${SWEEP}" data-duration="${TREE_LEN}" data-media-start="0" muted playsinline></video>
${qHtml}
      <div id="line"></div>
    </div>
    <div id="tabs" class="card" style="opacity:0">
      <img class="media" src="assets/tabs0.png" alt="" />
      <video id="tabsv" class="clip media" src="assets/tabs.mp4" data-start="${TABS.toFixed(2)}" data-duration="${TABS_LEN}" data-media-start="0" muted playsinline style="z-index:2"></video>
    </div>
${subs.map(([id, , html]) => `    <div id="${id}" class="sub">${html}</div>`).join("\n")}
    <div id="curtain">Et sur les onglets</div>
  </div>
  <script>
    const tl = gsap.timeline({ paused: true });
    tl.set("#curtain", { yPercent: 100 }, 0);
    tl.fromTo("#tree", { scale: 0.95, opacity: 0 }, { scale: 1, opacity: 1, duration: 0.6, ease: "expo.out" }, 0);
    tl.fromTo("#p1", { y: -12, opacity: 0 }, { y: 0, opacity: 1, duration: 0.4, ease: "expo.out" }, 0);
    ${qTw}
    ${subTw}
    // balayage : Revit → Revit + BIMaestro
    tl.to(".q", { scale: 0, duration: 0.2, ease: "power2.in" }, ${(SWEEP - 0.2).toFixed(2)});
    tl.to("#treev", { clipPath: "inset(0% 0% 0% 0%)", duration: ${W}, ease: "power3.inOut" }, ${SWEEP});
    tl.fromTo("#line", { y: 0, opacity: 1 }, { y: 952, opacity: 1, duration: ${W}, ease: "power3.inOut", immediateRender: false }, ${SWEEP});
    tl.to("#line", { opacity: 0, duration: 0.03 }, ${(SWEEP + W).toFixed(2)});
    tl.set("#tree", { className: "card on" }, ${SWEEP});
    tl.to("#p1", { opacity: 0, y: -12, duration: 0.2 }, ${SWEEP});
    tl.fromTo("#p2", { opacity: 0, y: 12 }, { opacity: 1, y: 0, duration: 0.35, ease: "expo.out" }, ${(SWEEP + 0.1).toFixed(2)});
    // rideau « Et sur les onglets »
    tl.fromTo("#curtain", { yPercent: 100 }, { yPercent: 0, duration: 0.45, ease: "power3.inOut" }, ${CURTAIN.toFixed(2)});
    tl.set("#tree", { opacity: 0 }, ${(CURTAIN + 0.5).toFixed(2)});
    tl.set("#tabs", { opacity: 1 }, ${(CURTAIN + 0.5).toFixed(2)});
    tl.to("#curtain", { yPercent: -100, duration: 0.45, ease: "power3.inOut" }, ${(TABS - 0.3).toFixed(2)});
    tl.fromTo("#tabs", { y: 60, rotation: 2 }, { y: 0, rotation: 0, duration: 0.6, ease: "expo.out" }, ${(TABS - 0.2).toFixed(2)});
    tl.to("#tabs", { scale: 1.06, duration: ${(TABS_LEN - 0.8).toFixed(2)}, ease: "none" }, ${(TABS + 0.4).toFixed(2)});
    tl.to(["#tabs", "#p2", "#s4"], { y: -80, opacity: 0, duration: 0.35, ease: "power2.in" }, ${(REEL - 0.35).toFixed(2)});
    window.__timelines["reel"] = tl;
  </script>
</template>
`;
writeFileSync("compositions/reel.html", reel);

const sfx = [["whoosh", 0.05]];
qs.forEach((_, i) => sfx.push(["pop", +(0.5 + i * 0.22).toFixed(2)]));
sfx.push(["click", SWEEP], ["whoosh", +CURTAIN.toFixed(2)], ["whoosh", +(TABS - 0.3).toFixed(2)],
  ["whoosh", +(REEL - 0.35).toFixed(2)], ["pop", +(REEL + 0.1).toFixed(2)]);
const vol = { click: 0.8, whoosh: 0.7, pop: 0.45 };
const audio = sfx.map(([n, s], i) =>
  `      <audio id="sx${i}" class="clip" src="assets/sfx/${n}.mp3" data-start="${s}" data-duration="${n === "click" ? 0.37 : 0.6}" data-track-index="${3 + (i % 2)}" data-volume="${vol[n]}"></audio>`).join("\n");

writeFileSync("index.html", `<!doctype html>
<html lang="fr">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=1080, height=1350" />
    <script src="https://cdn.jsdelivr.net/npm/gsap@3.15.0/dist/gsap.min.js"></script>
    <style>
      * { margin: 0; padding: 0; box-sizing: border-box; }
      html, body { width: 1080px; height: 1350px; overflow: hidden; background: #FFFFFF; }
      #root { position: relative; width: 100%; height: 100%; font-family: Inter; }
      #bg { position: absolute; inset: 0; background: #FFFFFF; }
      #bg-dots { position: absolute; inset: 0; background-image: radial-gradient(#E5E7EB 2px, transparent 2px); background-size: 40px 40px; opacity: .7; }
    </style>
  </head>
  <body>
    <div id="root" data-composition-id="main" data-start="0" data-duration="${TOTAL}" data-width="1080" data-height="1350">
      <div id="bg"></div>
      <div id="bg-dots"></div>
      <div id="reel" data-composition-id="reel" data-composition-src="compositions/reel.html" data-start="0" data-duration="${REEL}" data-track-index="1" data-track-kind="graphics" data-width="1080" data-height="1350"></div>
      <div id="outro" data-composition-id="outro" data-composition-src="compositions/outro.html" data-start="${REEL}" data-duration="${OUTRO}" data-track-index="1" data-track-kind="graphics" data-width="1080" data-height="1350"></div>
${audio}
    </div>
    <script>
      window.__timelines["main"] = gsap.timeline({ paused: true });
    </script>
  </body>
</html>
`);
console.log(`total ${TOTAL}s (reel ${REEL}s)`);
