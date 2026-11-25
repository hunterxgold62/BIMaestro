// « Couche par couche » — LinkedIn 4:5 (1080×1350). Un curseur coche les couches une à une
// (texte, fond, liseré, icônes, zébrage, contour) et la vraie palette Propriétés se construit.
// Images : capture/rushes/couches (couche k prise à 4,5+5k s).
// node gen.mjs → index.html + compositions/reel.html
import { writeFileSync } from "node:fs";

const W = 0.3, FIRST = 2.1, STEP = 1.75;
const layers = ["Texte coloré", "Fond dégradé", "Liseré", "Icônes", "Lignes alternées", "Contour"];
const CX = 840, Y0 = 300, DY = 122; // centre des boutons
const steps = layers.map((label, k) => ({ k, label, x: CX, y: Y0 + k * DY, t: +(FIRST + k * STEP).toFixed(2) }));
const LAST = steps.at(-1).t;
// 7e bouton : trois vraies lignes (découpées dans f0) montent dans la section « Mes paramètres » (f1).
const FAV = { label: "Ton paramètre", x: CX, y: Y0 + 6 * DY, t: +(LAST + STEP).toFixed(2) };
const SC = 560 / 476, RH = 24; // échelle image → carte, hauteur d'une ligne Revit
const moves = [["Hauteur", 312, 168], ["Contrainte", 288, 192], ["Commentaires", 840, 216]]; // [ligne, y avant, y après] en px image
const f2 = (v) => +v.toFixed(2);
const FINAL = f2(FAV.t + 2.1);
const REEL = +(FINAL + 2.4).toFixed(2);
const OUTRO = 2.8, TOTAL = +(REEL + OUTRO).toFixed(2);

const imgs = steps.map((s) => `      <img id="cv${s.k}" class="cv" src="assets/looks/${s.k === 5 ? "f0" : "c" + (s.k + 1)}.png" alt="" style="z-index:${s.k + 2}" />`).join("\n");
const favHtml = [
  ...moves.map((m, i) => `      <i id="ho${i}" class="hole" style="top:${(m[1] * SC).toFixed(1)}px"></i>`),
  `      <img id="cvF" class="cvf" src="assets/looks/f1.png" alt="" />`,
  ...moves.map((m, i) => `      <div id="mv${i}" class="mv" style="top:${(m[1] * SC).toFixed(1)}px"><img src="assets/looks/f0.png" alt="" style="top:${(-m[1] * SC).toFixed(1)}px" /></div>`),
].join("\n");
const chips = steps.map((s) =>
  `    <div id="ch${s.k}" class="chip" style="left:${s.x - 200}px;top:${s.y - 44}px"><i id="cb${s.k}" class="box"></i><span>${s.label}</span></div>`).join("\n")
  + `\n    <div id="chF" class="chip fav" style="left:${FAV.x - 200}px;top:${FAV.y - 44}px"><i id="cbF" class="box"></i><span>${FAV.label}</span></div>`;

const tw = [];
tw.push(`tl.fromTo("#card", { scale: 0.97 }, { scale: 1, duration: 0.6, ease: "expo.out" }, 0);`);
tw.push(`tl.fromTo(".chip", { x: 14 }, { x: 0, duration: 0.35, ease: "expo.out", stagger: 0.05 }, 0);`);
tw.push(`tl.fromTo("#sub", { opacity: 0, y: 14 }, { opacity: 1, y: 0, duration: 0.3, ease: "expo.out" }, 0.8);`);
tw.push(`tl.set("#cur", { x: 1180, y: 1420 }, 0);`);
tw.push(`tl.set("#ring", { scale: 0, opacity: 0 }, 0);`);
steps.forEach((s) => {
  const cx = s.x - 120, cy = s.y + 8;
  tw.push(`tl.to("#cur", { x: ${cx}, y: ${cy}, duration: 0.45, ease: "power3.inOut" }, ${+(s.t - 0.55).toFixed(2)});`);
  tw.push(`tl.fromTo("#ring", { x: ${cx}, y: ${cy}, scale: 0.3, opacity: 1 }, { x: ${cx}, y: ${cy}, scale: 1.8, opacity: 0, duration: 0.35, ease: "power2.out", immediateRender: false }, ${+(s.t - 0.08).toFixed(2)});`);
  tw.push(`tl.to("#ch${s.k}", { backgroundColor: "#0F5132", color: "#ffffff", borderColor: "#0F5132", duration: 0.15 }, ${+(s.t - 0.08).toFixed(2)});`);
  tw.push(`tl.fromTo("#cb${s.k}", { scale: 1 }, { scale: 1.35, duration: 0.1, ease: "power2.out" }, ${+(s.t - 0.08).toFixed(2)});`);
  tw.push(`tl.to("#cb${s.k}", { scale: 1, backgroundColor: "#7FD6A6", borderColor: "#7FD6A6", duration: 0.15, ease: "back.out(3)" }, ${+(s.t + 0.02).toFixed(2)});`);
  tw.push(`tl.fromTo("#cv${s.k}", { clipPath: "inset(0% 0% 100% 0%)" }, { clipPath: "inset(0% 0% 0% 0%)", duration: ${W}, ease: "power3.inOut" }, ${s.t});`);
  tw.push(`tl.fromTo("#line", { y: 0, opacity: 1 }, { y: 960, opacity: 1, duration: ${W}, ease: "power3.inOut", immediateRender: false }, ${s.t});`);
  tw.push(`tl.to("#line", { opacity: 0, duration: 0.03 }, ${+(s.t + W).toFixed(2)});`);
});
{ // clic sur « Ton paramètre », puis montée fluide des trois lignes
  const t = FAV.t, cx = FAV.x - 120, cy = FAV.y + 8;
  tw.push(`tl.fromTo("#chF", { opacity: 0, x: 30 }, { opacity: 1, x: 0, duration: 0.35, ease: "back.out(2)" }, ${f2(LAST + 0.35)});`);
  tw.push(`tl.to("#cur", { x: ${cx}, y: ${cy}, duration: 0.45, ease: "power3.inOut" }, ${f2(t - 0.55)});`);
  tw.push(`tl.fromTo("#ring", { x: ${cx}, y: ${cy}, scale: 0.3, opacity: 1 }, { x: ${cx}, y: ${cy}, scale: 1.8, opacity: 0, duration: 0.35, ease: "power2.out", immediateRender: false }, ${f2(t - 0.08)});`);
  tw.push(`tl.to("#chF", { backgroundColor: "#0F5132", color: "#ffffff", borderColor: "#0F5132", duration: 0.15 }, ${f2(t - 0.08)});`);
  tw.push(`tl.fromTo("#cbF", { scale: 1 }, { scale: 1.35, duration: 0.1, ease: "power2.out" }, ${f2(t - 0.08)});`);
  tw.push(`tl.to("#cbF", { scale: 1, backgroundColor: "#7FD6A6", borderColor: "#7FD6A6", duration: 0.15, ease: "back.out(3)" }, ${f2(t + 0.02)});`);
  tw.push(`tl.to("#cur", { x: 1180, y: 1420, duration: 0.4, ease: "power3.in" }, ${f2(t + 0.3)});`);
  // décollage : chaque ligne se soulève, un creux reste à sa place
  tw.push(`tl.set(".mv", { opacity: 1 }, ${f2(t + 0.05)});`);
  tw.push(`tl.to(".mv", { scale: 1.04, boxShadow: "0 14px 30px rgba(17,24,39,0.28), 0 0 0 3px rgba(127,214,166,1)", duration: 0.25, ease: "power2.out", stagger: 0.06 }, ${f2(t + 0.06)});`);
  tw.push(`tl.to(".hole", { opacity: 1, duration: 0.2, stagger: 0.06 }, ${f2(t + 0.06)});`);
  // vol : chaque ligne glisse vers sa place dans la nouvelle section
  moves.forEach((m, i) => tw.push(`tl.to("#mv${i}", { y: ${((m[2] - m[1]) * SC).toFixed(1)}, duration: 0.8, ease: "power3.inOut" }, ${f2(t + 0.35 + i * 0.1)});`));
  // la palette se réorganise sous les lignes (vraie image Revit)
  tw.push(`tl.to("#cvF", { opacity: 1, duration: 0.25, ease: "power1.inOut" }, ${f2(t + 0.85)});`);
  tw.push(`tl.to(".hole", { opacity: 0, duration: 0.15 }, ${f2(t + 1.0)});`);
  // atterrissage : l'ombre retombe, la vraie image reprend la main
  tw.push(`tl.to(".mv", { scale: 1, boxShadow: "0 0px 0px rgba(17,24,39,0), 0 0 0 0px rgba(127,214,166,0)", duration: 0.2, ease: "power2.in", stagger: 0.1 }, ${f2(t + 1.1)});`);
  tw.push(`tl.to(".mv", { opacity: 0, duration: 0.15 }, ${f2(t + 1.6)});`);
}
tw.push(`tl.to(["#title", "#sub"], { opacity: 0, y: -14, duration: 0.2 }, ${+(FINAL - 0.25).toFixed(2)});`);
tw.push(`tl.fromTo("#fin", { opacity: 0, y: 24 }, { opacity: 1, y: 0, duration: 0.35, ease: "expo.out" }, ${FINAL});`);
tw.push(`tl.fromTo(".chip", { scale: 1 }, { scale: 1.06, duration: 0.18, yoyo: true, repeat: 1, ease: "power2.out", stagger: 0.05 }, ${FINAL});`);
tw.push(`tl.to(["#card", ".chip", "#fin"], { y: -60, opacity: 0, duration: 0.35, ease: "power2.in" }, ${+(REEL - 0.35).toFixed(2)});`);

const reel = `<template>
  <style>
    #root { position: absolute; inset: 0; }
    #title, #fin { position: absolute; top: 46px; left: 40px; right: 40px; text-align: center; font-weight: 900; font-size: 64px; letter-spacing: -0.04em; color: #111827; }
    #title b, #fin b { color: #0F5132; }
    #sub { position: absolute; top: 124px; left: 40px; right: 40px; text-align: center; font-weight: 800; font-size: 38px; color: #6B7280; opacity: 0; }
    #fin { opacity: 0; }
    #card { position: absolute; left: 50px; top: 210px; width: 560px; height: 960px; border-radius: 26px; overflow: hidden; background: #fff; box-shadow: 0 30px 80px rgba(17,24,39,.18), 0 0 0 6px #0F5132; }
    .cv, #base { position: absolute; left: 0; top: 0; width: 560px; height: 1082px; }
    .cv { clip-path: inset(0% 0% 100% 0%); }
    #line { position: absolute; left: 0; right: 0; top: 0; height: 8px; background: #0F5132; box-shadow: 0 0 24px #7FD6A6; z-index: 60; opacity: 0; }
    .chip { position: absolute; width: 400px; height: 88px; border-radius: 999px; background: #fff; border: 3px solid #D1D5DB; display: flex; align-items: center; gap: 16px; padding: 0 26px; font-weight: 900; font-size: 34px; color: #111827; box-shadow: 0 12px 30px rgba(17,24,39,.08); }
    .hole { position: absolute; left: 0; width: 560px; height: ${(RH * SC).toFixed(1)}px; background: #E5E7EB; box-shadow: inset 0 0 0 2px #7FD6A6; z-index: 15; opacity: 0; }
    .cvf { position: absolute; left: 0; top: 0; width: 560px; height: 1082px; z-index: 20; opacity: 0; }
    .mv { position: absolute; left: 0; width: 560px; height: ${(RH * SC).toFixed(1)}px; overflow: hidden; z-index: 40; opacity: 0; }
    .mv img { position: absolute; left: 0; width: 560px; height: 1082px; }
    .chip.fav { border-style: dashed; border-color: #0F5132; opacity: 0; }
    .box { width: 32px; height: 32px; border-radius: 10px; border: 3px solid #9CA3AF; background: #fff; flex: none; }
    #cur { position: absolute; left: 0; top: 0; width: 64px; height: 64px; z-index: 90; }
    #ring { position: absolute; left: -40px; top: -40px; width: 80px; height: 80px; border-radius: 50%; border: 6px solid #0F5132; z-index: 89; }
  </style>
  <div id="root" data-composition-id="reel" data-width="1080" data-height="1350">
    <div id="title">Construis ta <b>palette</b></div>
    <div id="sub">couche par couche.</div>
    <div id="fin">Tes couches. <b>Ton ordre.</b></div>
${chips}
    <div id="card">
      <img id="base" src="assets/looks/c0.png" alt="" />
${imgs}
${favHtml}
      <div id="line"></div>
    </div>
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
steps.forEach((s) => sfx.push(["click", +(s.t - 0.08).toFixed(2)]));
sfx.push(["click", f2(FAV.t - 0.08)], ["whoosh", f2(FAV.t + 0.35)]);
sfx.push(["pop", FINAL], ["whoosh", +(REEL - 0.35).toFixed(2)], ["pop", +(REEL + 0.1).toFixed(2)]);
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
console.log(`total ${TOTAL}s`);
