// Vidéo 2 « Configurateur » : on active les réglages un par un, l'arborescence réelle suit.
// node gen.mjs → index.html + compositions/reel.html
import { writeFileSync } from "node:fs";

const W = 0.3, FIRST = 1.7, STEP = 1.7;
// [libellé, n° de variante dans le rush config, centre x, centre y du bouton]
const chips = [
  ["Fond", 1, 230, 250], ["Couleurs", 2, 540, 250], ["Icônes", 3, 850, 250],
  ["Badges", 4, 385, 345], ["Mode nuit", 5, 695, 345],
];
const steps = chips.map(([label, vk, x, y], k) => ({ k, label, vk, x, y, t: +(FIRST + k * STEP).toFixed(2) }));
const LAST = steps.at(-1).t;
const FINAL = +(LAST + 2.2).toFixed(2);
const REEL = +(FINAL + 2.0).toFixed(2);
const OUTRO = 2.8, TOTAL = +(REEL + OUTRO).toFixed(2);
const media = (vk, offset = 0) => +(1.1 + 5 * vk + 1.6 + offset).toFixed(2);

const vids = [
  `      <video id="cv0" class="clip cv" src="assets/config.mp4" data-start="0" data-duration="${steps[0].t + W}" data-media-start="${media(0, -1)}" muted playsinline style="z-index:1"></video>`,
  ...steps.map((s, i) => {
    const end = i === steps.length - 1 ? REEL : steps[i + 1].t + W;
    return `      <video id="cv${s.k + 1}" class="clip cv" src="assets/config.mp4" data-start="${s.t}" data-duration="${+(end - s.t).toFixed(2)}" data-media-start="${media(s.vk)}" muted playsinline style="z-index:${s.k + 2}"></video>`;
  }),
].join("\n");
const chipHtml = steps.map((s) =>
  `    <div id="ch${s.k}" class="chip" style="left:${s.x - 135}px;top:${s.y - 38}px"><i id="cb${s.k}" class="box"></i><span>${s.label}</span></div>`).join("\n");

const tw = [];
tw.push(`tl.fromTo("#ccard", { y: 700, rotation: 4 }, { y: 0, rotation: 0, duration: 0.55, ease: "expo.out" }, 0);`);
tw.push(`tl.fromTo(".chip", { opacity: 0, y: -24, scale: 0.8 }, { opacity: 1, y: 0, scale: 1, duration: 0.3, ease: "back.out(2)", stagger: 0.06 }, 0.25);`);
tw.push(`tl.set("#cur", { x: 1200, y: 1500 }, 0);`);
tw.push(`tl.set(["#ring"], { scale: 0, opacity: 0 }, 0);`);
steps.forEach((s) => {
  tw.push(`tl.to("#cur", { x: ${s.x + 40}, y: ${s.y + 10}, duration: 0.45, ease: "power3.inOut" }, ${+(s.t - 0.55).toFixed(2)});`);
  tw.push(`tl.fromTo("#ring", { x: ${s.x + 40}, y: ${s.y + 10}, scale: 0.3, opacity: 1 }, { x: ${s.x + 40}, y: ${s.y + 10}, scale: 1.8, opacity: 0, duration: 0.35, ease: "power2.out", immediateRender: false }, ${+(s.t - 0.08).toFixed(2)});`);
  tw.push(`tl.to("#ch${s.k}", { backgroundColor: "#0F5132", color: "#ffffff", borderColor: "#0F5132", duration: 0.15 }, ${+(s.t - 0.08).toFixed(2)});`);
  tw.push(`tl.fromTo("#cb${s.k}", { scale: 1 }, { scale: 1.35, duration: 0.1, ease: "power2.out" }, ${+(s.t - 0.08).toFixed(2)});`);
  tw.push(`tl.to("#cb${s.k}", { scale: 1, backgroundColor: "#7FD6A6", borderColor: "#7FD6A6", duration: 0.15, ease: "back.out(3)" }, ${+(s.t + 0.02).toFixed(2)});`);
  tw.push(`tl.fromTo("#cv${s.k + 1}", { clipPath: "inset(0% 0% 100% 0%)" }, { clipPath: "inset(0% 0% 0% 0%)", duration: ${W}, ease: "power3.inOut" }, ${s.t});`);
  tw.push(`tl.fromTo("#cline", { y: 0, opacity: 1 }, { y: 790, opacity: 1, duration: ${W}, ease: "power3.inOut", immediateRender: false }, ${s.t});`);
  tw.push(`tl.to("#cline", { opacity: 0, duration: 0.03 }, ${+(s.t + W).toFixed(2)});`);
});
tw.push(`tl.to("#cur", { x: 1200, y: 1500, duration: 0.4, ease: "power3.in" }, ${+(LAST + 0.4).toFixed(2)});`);
tw.push(`tl.fromTo("#cfin", { opacity: 0, y: 30 }, { opacity: 1, y: 0, duration: 0.35, ease: "expo.out" }, ${FINAL});`);
tw.push(`tl.to(["#ccard", ".chip", "#cfin"], { y: -60, opacity: 0, duration: 0.35, ease: "power2.in" }, ${+(REEL - 0.35).toFixed(2)});`);

const reel = `<template>
  <style>
    #root { position: absolute; inset: 0; }
    .chip { position: absolute; width: 270px; height: 76px; border-radius: 999px; background: #fff; border: 3px solid #D1D5DB; display: flex; align-items: center; gap: 14px; padding: 0 22px; font-weight: 900; font-size: 32px; color: #111827; box-shadow: 0 10px 30px rgba(17,24,39,.08); }
    .box { width: 30px; height: 30px; border-radius: 9px; border: 3px solid #9CA3AF; background: #fff; flex: none; }
    #ccard { position: absolute; left: 230px; top: 430px; width: 620px; height: 790px; border-radius: 28px; overflow: hidden; background: #fff; box-shadow: 0 30px 80px rgba(17,24,39,.18), 0 0 0 2px #E5E7EB; }
    .cv { position: absolute; left: 0; top: 0; width: 620px; height: 1469px; object-fit: fill; }
    #cline { position: absolute; left: 0; right: 0; top: 0; height: 8px; background: #0F5132; box-shadow: 0 0 24px #7FD6A6; z-index: 50; opacity: 0; }
    #cbar { position: absolute; left: 0; top: 0; bottom: 0; width: 10px; background: #0F5132; z-index: 51; }
    #cur { position: absolute; left: 0; top: 0; width: 64px; height: 64px; z-index: 90; }
    #ring { position: absolute; left: -40px; top: -40px; width: 80px; height: 80px; border-radius: 50%; border: 6px solid #0F5132; z-index: 89; }
    #cfin { position: absolute; top: 1245px; left: 60px; right: 60px; text-align: center; font-weight: 900; font-size: 60px; letter-spacing: -0.03em; color: #111827; opacity: 0; z-index: 80; text-shadow: 0 0 30px #fff, 0 0 30px #fff; }
    #cfin b { color: #0F5132; }
    #ctitle { position: absolute; top: 130px; left: 0; right: 0; text-align: center; font-weight: 900; font-size: 40px; color: #6B7280; letter-spacing: -0.01em; }
  </style>
  <div id="root" data-composition-id="reel" data-width="1080" data-height="1920">
    <div id="ctitle">Apparence BIMaestro</div>
${chipHtml}
    <div id="ccard">
${vids}
      <div id="cline"></div>
      <div id="cbar"></div>
    </div>
    <div id="cfin">Ton arborescence.<br /><b>Tes règles.</b></div>
    <div id="ring"></div>
    <div id="cur"><svg width="64" height="64" viewBox="0 0 24 24"><path d="M3 2l7 19 2.5-7.5L20 11z" fill="#fff" stroke="#111827" stroke-width="1.6" stroke-linejoin="round"/></svg></div>
  </div>
  <script>
    const tl = gsap.timeline({ paused: true });
    ${tw.join("\n    ")}
    window.__timelines["reel"] = tl;
  </script>
</template>
`;
writeFileSync("compositions/reel.html", reel);

const sfx = [["whoosh", 0.05]];
steps.forEach((s) => sfx.push(["click", +(s.t - 0.08).toFixed(2)], ["pop", +(s.t + 0.05).toFixed(2)]));
sfx.push(["pop", FINAL], ["whoosh", +(REEL - 0.35).toFixed(2)], ["pop", +(REEL + 0.1).toFixed(2)]);
const audio = sfx.map(([n, s], i) =>
  `      <audio id="sx${i}" class="clip" src="assets/sfx/${n}.mp3" data-start="${s}" data-duration="${n === "click" ? 0.37 : 0.6}" data-track-index="${3 + (i % 2)}" data-volume="${n === "pop" ? 0.45 : n === "click" ? 0.8 : 0.7}"></audio>`).join("\n");

writeFileSync("index.html", `<!doctype html>
<html lang="fr">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=1080, height=1920" />
    <script src="https://cdn.jsdelivr.net/npm/gsap@3.15.0/dist/gsap.min.js"></script>
    <style>
      * { margin: 0; padding: 0; box-sizing: border-box; }
      html, body { width: 1080px; height: 1920px; overflow: hidden; background: #FFFFFF; }
      #root { position: relative; width: 100%; height: 100%; font-family: Inter; }
      #bg { position: absolute; inset: 0; background: #FFFFFF; }
      #bg-dots { position: absolute; inset: 0; background-image: radial-gradient(#E5E7EB 2px, transparent 2px); background-size: 40px 40px; opacity: .7; }
    </style>
  </head>
  <body>
    <div id="root" data-composition-id="main" data-start="0" data-duration="${TOTAL}" data-width="1080" data-height="1920">
      <div id="bg"></div>
      <div id="bg-dots"></div>
      <div id="reel" data-composition-id="reel" data-composition-src="compositions/reel.html" data-start="0" data-duration="${REEL}" data-track-index="1" data-track-kind="graphics" data-width="1080" data-height="1920"></div>
      <div id="outro" data-composition-id="outro" data-composition-src="compositions/outro.html" data-start="${REEL}" data-duration="${OUTRO}" data-track-index="1" data-track-kind="graphics" data-width="1080" data-height="1920"></div>
${audio}
    </div>
    <script>
      window.__timelines["main"] = gsap.timeline({ paused: true });
    </script>
  </body>
</html>
`);
console.log(`total ${TOTAL}s`);
