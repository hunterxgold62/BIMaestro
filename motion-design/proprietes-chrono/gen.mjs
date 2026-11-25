// Vidéo 1C « Chrono Propriétés » — LinkedIn 4:5 (1080×1350).
// « Trouve Commentaires » : à gauche on balaie toute la palette Revit, à droite BIMaestro l'a déjà en haut.
// Rush : capture/rushes/proprietes (look k → media 3,5+5k). Look 5 = Mes paramètres, look 7 = Tout en un.
// node gen.mjs  → écrit index.html et compositions/reel.html
import { writeFileSync } from "node:fs";

const SC = 460 / 476;                    // échelle rush → carte
const ROW_BASE = Math.round(840 * SC);   // ligne « Commentaires » dans Revit de base
const ROW_MES = Math.round(216 * SC);     // ligne « Commentaires » dans Mes paramètres
const ROW_FIRST = Math.round(168 * SC);  // première ligne de la palette
const W = 0.3;
const T = {
  rightWipe: 0.8, rightFound: 1.15, scanFrom: 0.5, scanTo: 4.0,
  cap2: 4.3, tout: 6.4, cap3: 6.5, split: 8.7, cap4: 8.9,
};
const REEL = 10.9, OUTRO = 2.8, TOTAL = +(REEL + OUTRO).toFixed(2);

const reel = `<template>
  <style>
    #root { position: absolute; inset: 0; }
    #hook { position: absolute; top: 34px; left: 40px; right: 40px; text-align: center; font-weight: 900; font-size: 66px; letter-spacing: -0.04em; color: #111827; }
    #hook b { color: #0F5132; }
    .bcard { position: absolute; top: 230px; width: 460px; height: 860px; border-radius: 26px; overflow: hidden; background: #fff; box-shadow: 0 30px 80px rgba(17,24,39,.18), 0 0 0 2px #E5E7EB; }
    #bl { left: 60px; }
    #br { left: 560px; box-shadow: 0 30px 80px rgba(17,24,39,.22), 0 0 0 6px #0F5132; }
    .bimg, .bv { position: absolute; left: 0; top: 0; width: 460px; height: 1090px; object-fit: fill; }
    #bl-after { clip-path: inset(0% 100% 0% 0%); z-index: 8; }
    .hl { position: absolute; left: 4px; right: 4px; height: 28px; border-radius: 6px; border: 4px solid #0F5132; background: rgba(127,214,166,.28); box-shadow: 0 0 18px rgba(127,214,166,.9); z-index: 20; opacity: 0; }
    #hl-l { top: ${ROW_FIRST - 3}px; border-color: #6B7280; background: rgba(107,114,128,.14); box-shadow: none; }
    #hl-r { top: ${ROW_MES - 3}px; }
    #bline { position: absolute; left: 0; right: 0; top: 0; height: 7px; background: #0F5132; box-shadow: 0 0 22px #7FD6A6; z-index: 60; opacity: 0; }
    #bvline { position: absolute; top: 0; bottom: 0; left: 0; width: 7px; background: #0F5132; box-shadow: 0 0 22px #7FD6A6; z-index: 60; opacity: 0; }
    .bpill { position: absolute; top: 150px; width: 460px; text-align: center; }
    .bpill span { display: inline-block; font-weight: 900; font-size: 34px; padding: 8px 22px; border-radius: 999px; }
    #bpl { left: 60px; } #bpl span { background: #E5E7EB; color: #374151; }
    #bpr { left: 560px; } #bpr span { background: #0F5132; color: #fff; }
    .chrono { position: absolute; top: 1110px; width: 460px; text-align: center; }
    .chrono span { display: inline-block; font-weight: 900; font-size: 44px; padding: 6px 22px; border-radius: 16px; font-variant-numeric: tabular-nums; }
    #cl { left: 60px; } #cl span { background: #F3F4F6; color: #374151; }
    #cr { left: 560px; } #cr span { background: #D7F5E5; color: #0F5132; }
    .bsub { position: absolute; top: 1205px; left: 40px; right: 40px; text-align: center; font-weight: 900; font-size: 56px; letter-spacing: -0.03em; color: #111827; opacity: 0; white-space: nowrap; }
    .bsub b { color: #0F5132; }
  </style>
  <div id="root" data-composition-id="reel" data-width="1080" data-height="1350">
    <div id="hook">Trouve <b>« Commentaires »</b>.</div>
    <div id="bpl" class="bpill"><span>Revit</span></div>
    <div id="bpr" class="bpill"><span>Revit + BIMaestro</span></div>
    <div id="bl" class="bcard">
      <img class="bimg" src="assets/base.png" alt="" />
      <img id="bl-after" class="bimg" src="assets/tout.png" alt="" />
      <div id="hl-l" class="hl"></div>
      <div id="bvline"></div>
    </div>
    <div id="br" class="bcard">
      <img class="bimg" src="assets/base.png" alt="" style="z-index:1" />
      <img id="bv1" class="bimg" src="assets/mes.png" alt="" style="z-index:2; clip-path: inset(0% 0% 100% 0%)" />
      <img id="bv2" class="bimg" src="assets/tout.png" alt="" style="z-index:3; clip-path: inset(0% 0% 100% 0%)" />
      <div id="hl-r" class="hl"></div>
      <div id="bline"></div>
    </div>
    <div id="cl" class="chrono"><span id="cl-t">0,0 s</span></div>
    <div id="cr" class="chrono"><span id="cr-t">0,0 s</span></div>
    <div id="s2" class="bsub">BIMaestro : <b>déjà tout en haut.</b></div>
    <div id="s3" class="bsub"><b>En couleur</b>, dans ton ordre.</div>
    <div id="s4" class="bsub">Et toi, <b>tu cherches encore ?</b></div>
  </div>
  <script>
    const tl = gsap.timeline({ paused: true });
    const fmt = (v) => v.toFixed(1).replace(".", ",") + " s";
    // ouverture : tout est lisible dès l'image 0
    tl.fromTo(["#bl", "#br"], { scale: 0.96 }, { scale: 1, duration: 0.6, ease: "expo.out", stagger: 0.05 }, 0);
    tl.fromTo("#hook", { y: 10 }, { y: 0, duration: 0.4, ease: "expo.out" }, 0);
    // chronos
    const cl = { v: 0 }, cr = { v: 0 };
    tl.to(cl, { v: ${T.scanTo - T.scanFrom}, duration: ${T.scanTo - T.scanFrom}, ease: "none", onUpdate: () => { document.getElementById("cl-t").textContent = fmt(cl.v); } }, ${T.scanFrom});
    tl.to(cr, { v: ${+(T.rightFound - T.scanFrom).toFixed(2)}, duration: ${+(T.rightFound - T.scanFrom).toFixed(2)}, ease: "none", onUpdate: () => { document.getElementById("cr-t").textContent = fmt(cr.v); } }, ${T.scanFrom});
    tl.fromTo("#cr span", { scale: 1 }, { scale: 1.18, duration: 0.15, yoyo: true, repeat: 1, ease: "power2.out" }, ${T.rightFound});
    // gauche : on balaie la palette ligne à ligne
    tl.to("#hl-l", { opacity: 1, duration: 0.1 }, ${T.scanFrom});
    tl.to("#hl-l", { y: ${ROW_BASE - ROW_FIRST}, duration: ${T.scanTo - T.scanFrom}, ease: "steps(27)" }, ${T.scanFrom});
    tl.to("#hl-l", { borderColor: "#0F5132", backgroundColor: "rgba(127,214,166,.28)", duration: 0.15 }, ${T.scanTo});
    tl.fromTo("#cl span", { scale: 1 }, { scale: 1.12, duration: 0.15, yoyo: true, repeat: 1, ease: "power2.out" }, ${T.scanTo});
    // droite : balayage vert vers Mes paramètres, Commentaires déjà en haut
    tl.fromTo("#bv1", { clipPath: "inset(0% 0% 100% 0%)" }, { clipPath: "inset(0% 0% 0% 0%)", duration: ${W}, ease: "power3.inOut" }, ${T.rightWipe});
    tl.fromTo("#bline", { y: 0, opacity: 1 }, { y: 860, opacity: 1, duration: ${W}, ease: "power3.inOut", immediateRender: false }, ${T.rightWipe});
    tl.to("#bline", { opacity: 0, duration: 0.03 }, ${T.rightWipe + W});
    tl.fromTo("#hl-r", { opacity: 0, scale: 1.15 }, { opacity: 1, scale: 1, duration: 0.25, ease: "back.out(2.5)" }, ${T.rightFound});
    // textes
    tl.to("#hook", { opacity: 0, y: -16, duration: 0.2 }, ${T.cap2 - 0.1});
    tl.fromTo("#s2", { opacity: 0, y: 30 }, { opacity: 1, y: 0, duration: 0.3, ease: "expo.out" }, ${T.cap2});
    tl.to("#s2", { opacity: 0, y: -20, duration: 0.2 }, ${T.cap3 - 0.25});
    // droite : look « Tout en un »
    tl.to("#hl-r", { opacity: 0, duration: 0.15 }, ${T.tout - 0.1});
    tl.fromTo("#bv2", { clipPath: "inset(0% 0% 100% 0%)" }, { clipPath: "inset(0% 0% 0% 0%)", duration: ${W}, ease: "power3.inOut" }, ${T.tout});
    tl.fromTo("#bline", { y: 0, opacity: 1 }, { y: 860, opacity: 1, duration: ${W}, ease: "power3.inOut", immediateRender: false }, ${T.tout});
    tl.to("#bline", { opacity: 0, duration: 0.03 }, ${T.tout + W});
    tl.fromTo("#s3", { opacity: 0, y: 30 }, { opacity: 1, y: 0, duration: 0.3, ease: "expo.out" }, ${T.cap3});
    tl.to("#s3", { opacity: 0, y: -20, duration: 0.2 }, ${T.split - 0.25});
    // finale : le côté Revit bascule aussi
    tl.to("#hl-l", { opacity: 0, duration: 0.15 }, ${T.split - 0.1});
    tl.to(["#cl", "#cr"], { opacity: 0, duration: 0.2 }, ${T.split - 0.1});
    tl.to("#bpl span", { backgroundColor: "#0F5132", color: "#ffffff", duration: 0.3 }, ${T.split});
    tl.to("#bl-after", { clipPath: "inset(0% 0% 0% 0%)", duration: 0.5, ease: "power3.inOut" }, ${T.split});
    tl.fromTo("#bvline", { x: 0, opacity: 1 }, { x: 460, opacity: 1, duration: 0.5, ease: "power3.inOut" }, ${T.split});
    tl.to("#bvline", { opacity: 0, duration: 0.03 }, ${T.split + 0.5});
    tl.fromTo("#s4", { opacity: 0, y: 30 }, { opacity: 1, y: 0, duration: 0.3, ease: "expo.out" }, ${T.cap4});
    tl.to(["#bl", "#br", "#bpl", "#bpr", "#s4"], { y: -80, opacity: 0, duration: 0.35, ease: "power2.in" }, ${REEL - 0.35});
    window.__timelines["reel"] = tl;
  </script>
</template>
`;
writeFileSync("compositions/reel.html", reel);

const sfx = [["whoosh", 0.05], ["click", T.rightWipe], ["pop", T.rightFound], ["pop", T.scanTo],
  ["click", T.tout], ["whoosh", T.split], ["whoosh", +(REEL - 0.35).toFixed(2)], ["pop", +(REEL + 0.1).toFixed(2)]];
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
