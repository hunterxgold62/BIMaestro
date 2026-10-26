"""Check guide/UI contracts without starting Revit: python scripts/tutorial-tests/Verify.py."""
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2] / "BIMaestro"


def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")


def names(path):
    return {value for element in ET.fromstring(read(path)).iter()
            for key, value in element.attrib.items()
            if key in ("Name", "{http://schemas.microsoft.com/winfx/2006/xaml}Name")}


windows = {
    "reservation": "commands/Réservation auto/Résa auto V3/ReservationV3Window.xaml",
    "pipe-calculation": "commands/Calcul des canalisations/canalisation V2/PipeSystemSelectionWindowV2.xaml",
    "organizer": "commands/renuméroté élément/ElementRenameWindow.xaml",
    "family-browser": "commands/Dossier famille/FamilyBrowserPlugin.xaml",
    "history": "commands/qui est le coupable/ElementHistoryWindow.xaml",
    "clash-3d": "commands/analyse erreur maquette 3D/SmartCheckWindow.xaml",
}
catalog = read("commands/Tutoriels/DemoTourService.cs")
quoted = r'"(?:[^"\\]|\\.)*"'
step_pattern = r'new DemoStep\(' + quoted + r',\s*' + quoted + r',\s*"([^"]+)"'
errors = []
checked = 0
for tour, path in windows.items():
    targets = names(path)
    if tour == "family-browser":
        # Runtime-generated cards and native Revit ribbon controls have explicit resolvers.
        for target, resolver in {
            "TutorialFavoriteStar": "FindTutorialFavoriteStar",
            "TutorialMeetingFolderCard": "FindTutorialMeetingFolderCard",
        }.items():
            browser = read("commands/Dossier famille/FamilyBrowserPlugin.xaml.cs")
            if resolver + "(" in catalog and resolver + "(" in browser:
                targets.add(target)
        native = read("commands/Tutoriels/FamilyRibbonTutorialGuide.cs")
        targets.update(re.findall(r'"(Revit\w+)"', native))
    blocks = re.findall(r'\["' + tour + r'"\] = new\[\]\s*\{(.*?)\n\s*\}', catalog, re.S)
    if tour == "organizer":
        blocks += re.findall(r'OrganizerRotatedSteps =\s*\{(.*?)\n\s*\}', catalog, re.S)
    for block in blocks:
        for target in re.findall(step_pattern, block):
            checked += 1
            if target not in targets:
                errors.append(f"{tour}: missing UI target {target}")

# The appearance guide uses compiled WPF fields instead of string targets.
appearance = read("les couleurs/Personnalisation/ColorPreferencesWindow.Tutorial.cs")
appearance_names = names("les couleurs/Personnalisation/ColorPreferencesWindow.xaml")
for target in re.findall(r'target = (\w+);', appearance):
    checked += 1
    if target not in appearance_names:
        errors.append(f"colors: missing UI target {target}")
count = int(re.search(r'AppearanceGuideStepCount = (\d+)', appearance)[1])
titles = re.findall(r'title = "(\d+)/(\d+) ·', appearance)
if {int(index) for index, _ in titles} != set(range(1, count + 1)):
    errors.append("colors: incomplete step numbering")
if any(int(total) != count for _, total in titles):
    errors.append("colors: outdated step totals")

# Ribbon IDs must still exist after ribbon commands are moved or renamed.
onboarding = read("les couleurs/Personnalisation/AppearanceOnboarding.cs")
button_choices = onboarding.split('_buttonId = tourId ==', 1)[1].split('_buttonLabel =', 1)[0]
ribbon = re.sub(r'//[^\n]*', '', read("app et excel/Touche/AppUI.cs"))
for button in re.findall(r'\? "([^"]+)"|: "([^"]+)";', button_choices):
    button_id = next(value for value in button if value)
    if '"' + button_id + '"' not in ribbon:
        errors.append(f"ribbon: missing command {button_id}")

if errors:
    raise SystemExit("\n".join(errors))
print(f"PASS: {checked} tutorial targets, appearance numbering ({count} steps), ribbon commands.")
