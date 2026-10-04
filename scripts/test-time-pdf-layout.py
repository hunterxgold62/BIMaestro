"""Check the generated report's real glyph bounds with pdfplumber."""
import sys
from pathlib import Path

import pdfplumber

folder = Path(sys.argv[1])
for path in sorted(folder.glob("time-report-*.pdf")):
    with pdfplumber.open(path) as document:
        text = "\n".join(page.extract_text() or "" for page in document.pages)
        for number, page in enumerate(document.pages, 1):
            for char in page.chars:
                assert char["x0"] >= 35.5, (path.name, number, "left overflow", char)
                assert char["x1"] <= 559.5, (path.name, number, "right overflow", char)
                assert 14 <= char["top"] < char["bottom"] <= 817, (path.name, number, "vertical overflow", char)
            assert f"Page {number} / {len(document.pages)}" in (page.extract_text() or "")
            # Check actual text boxes, including neighboring columns, for overlaps.
            words = page.extract_words()
            for i, left in enumerate(words):
                for right in words[i + 1:]:
                    overlap_x = min(left["x1"], right["x1"]) - max(left["x0"], right["x0"])
                    overlap_y = min(left["bottom"], right["bottom"]) - max(left["top"], right["top"])
                    assert overlap_x <= .5 or overlap_y <= .5, (path.name, number, "overlapping words", left, right)
        if path.name == "time-report-sample.pdf":
            assert len(document.pages) == 1
            assert "Total : 1 h 22" in text
            assert text.index("Total :") < text.index("Détail de la sélection") < text.index("Maquette / famille")
        if path.name == "time-report-long.pdf":
            compact = "".join(text.split())
            assert all(marker in compact for marker in ("NOM_FIN", "CHEMIN_FIN.rvt", "FILTRE_FIN"))
        if path.name == "time-report-empty.pdf":
            assert "Aucune activité" in text
        print(f"{path.name}: {len(document.pages)} pages, margins, text overlap and page numbers passed")
