# Automatic standing position

`MaintenanceOperatorPlacement.Suggest(document, source, profile, progress)`
captures nearby geometry once, reads actual upward floor/stair/landing/terrain
faces, tries positions around the command, and checks a level nine-point foot
rectangle, the entire represented standing body's movement and hand reach.
Native solid intersections verify each returned candidate. Pipes, equipment,
equipment plinths and generic furniture cannot become floor supports.

`Validate(document, source, profile, progress)` applies the same checks to
`profile.OperatorOrigin`. It requires the point to be on the actual support
surface. `IsWalkingSurface(element)` is available for the manual picking filter;
a linked reference must first resolve its linked element.

The result's `FootPoint` is null when no supported, reachable and unoccupied
position is found. `Support` identifies the floor and direct link, `Confidence`
indicates whether capture was complete, and `Alternatives` contains up to six
verified candidates. `Warnings` report unresolved capture/placement issues;
`Assumptions` describe normal scope without invalidating a successful placement.
A working position is not an
approach route, a stability/structure study, or an ergonomics certification.

For chains, `Suggest` can raise `SuggestedWorkHeight` (internal feet) just enough
to leave the requested pull stroke above the bottom of the loop, then checks
reach again. Each alternative carries its own height. The input profile is not
modified. `Validate` keeps `profile.WorkHeight` exactly as supplied; the UI must
apply a suggested value visibly before using it for the intervention animation.

Support samples are geometric, not normative dimensions. The represented feet
must remain nearly level; the operator cannot stand on a pipe, a machine block,
an inferred platform or a surface several metres below an unreachable command.
For filter/actuator removal, samples also cover the modeled horizontal retreat.
No step-climbing or crouching is inferred.

Run the portable ray and support regressions:

```powershell
dotnet run --project scripts\maintenance-placement-tests\MaintenancePlacement.Tests.csproj --configuration Release
```

The Revit API adapter can be compiled without launching Revit:

```powershell
dotnet build scripts\motor-door-tests\MaintenancePlacement.Compile.csproj --configuration Release
```

End-to-end native tests must additionally exercise actual model geometry,
category interpretation, manual points and body clearance.
