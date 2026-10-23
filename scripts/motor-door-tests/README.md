# Motor route through intermediate doors

`MotorRouteGeometry` keeps intermediate doors as obstacles by default. Its final
optional constructor argument `allowIntermediateDoors` enables the explicit
assumption that the target and intermediate doors can be opened and are open.
The UI must expose this assumption when it passes `true`.

The door family and its nested components are excluded, while the actual wall
solid and its opening remain in the collision model. If nominal width and height
parameters exist, `MotorDoorPortal` additionally checks the complete swept motor
box in the thickness of the doorway, including oblique translations and the
inflated samples used for turns. Door coordinates are transformed for linked
models. The nominal aperture is assumed centered on the family's insertion point
with its base at the insertion elevation.

A door hosted by a wall may still be considered open when nominal dimensions
cannot be obtained; the wall opening remains checked and this limitation is
reported in `Unknown`. Without dimensions or a wall host, the door stays an
obstacle. No check certifies the remaining clear passage with a real open leaf,
the frame, locking, fire-door conditions or the act of opening the door.

Run the standalone geometry regression checks (no Revit required):

```powershell
dotnet run --project scripts\motor-door-tests\MotorDoor.Tests.csproj --configuration Release
```

The checks cover nominal clearance, width, height, sill, offsets, perpendicular
orientations, oblique approaches, sideways motion, linked coordinates and other
doorways on the same plane. Actual wall collisions continue to be checked by
the production route geometry.

Compile the route adapter against the installed Revit 2025 API without launching
Revit (stubs only adapt unrelated envelope operations for compilation):

```powershell
dotnet build scripts\motor-door-tests\MotorRoute.Compile.csproj --configuration Release
```

Pass `--property:RevitApiDirectory="C:/Program Files/Autodesk/Revit 2026"` to use
another compatible API. Full add-in builds and in-Revit model checks remain
necessary to validate the complete integration.

`NativeDoorGeometry.csproj` is a separate, opt-in in-Revit fixture using the
production assembly through reflection and real Revit solids. Its application
class is `BIMaestro.MotorTests.NativeDoorGeometry`. An explicitly launched empty
test process with `BIMAESTRO_MOTOR_DOOR_CHECK=1` runs nine assertions and writes
`native-door-report.json` beside its assembly. It closes that empty test process
afterward. The fixture checks native translation, search, closed leaf, undersize
door, intermediate turning clearance and transformed portal coordinates; it does
not construct or validate actual door-family parameters. Building the fixture
does not launch Revit or run these native assertions.
