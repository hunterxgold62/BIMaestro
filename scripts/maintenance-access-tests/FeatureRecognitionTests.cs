using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text.Json;
using Autodesk.Revit.DB;
using BIMaestro.MepBooster;

internal static class FeatureRecognitionTests
{
    private static int _checks;
    private static double Mm(double value) => value / 304.8;
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); _checks++; }
    private static MaintenanceFeatureObservation Circle(XYZ center, XYZ axis, double radius, string component = "corps", bool namedPart = false,
        double thickness = 0, bool cylinder = false)
        => new MaintenanceFeatureObservation { Center = center, Axis = axis, Radius = radius, Component = component, Origin = "fixture géométrique",
            Coverage = 2 * Math.PI, AxialLength = thickness, Cylinder = cylinder, ComponentExplicit = namedPart };
    private static MaintenanceFeatureConnector[] Connectors()
        => new[] { new MaintenanceFeatureConnector { Center = new XYZ(-Mm(250), 0, 0), Axis = -XYZ.BasisX, Radius = Mm(50) },
            new MaintenanceFeatureConnector { Center = new XYZ(Mm(250), 0, 0), Axis = XYZ.BasisX, Radius = Mm(50) } };
    private static MaintenanceFeatureDimension Dimension(MaintenanceFeatureDimensionKind kind, double value)
        => new MaintenanceFeatureDimension { Kind = kind, Value = value, Name = "fixture " + kind, Component = "composant", Origin = "paramètre de famille" };
    private static MaintenanceFeatureSuggestion Recognize(MaintenanceAccessOperation operation, IEnumerable<MaintenanceFeatureObservation> geometry,
        params MaintenanceFeatureDimension[] parameters) => MaintenanceFeatureRecognition.Recognize(operation, geometry, parameters, Connectors());

    internal static int Run()
    {
        var empty = Recognize(MaintenanceAccessOperation.HandwheelValve, Array.Empty<MaintenanceFeatureObservation>());
        Check(!empty.HasCommandPoint && empty.Confidence == MaintenanceFeatureConfidence.None, "A name without geometry cannot invent a command point");
        Check(MaintenanceFeatureRecognition.DimensionKind("DN") == null && MaintenanceFeatureRecognition.DimensionKind("D") == null,
            "A nominal pipe diameter is not a handwheel dimension");
        Check(MaintenanceFeatureRecognition.DimensionKind("Volant rayon arc") == null && MaintenanceFeatureRecognition.DimensionKind("Volant rayon fixation") == null,
            "The rim section and hub radius are not the wheel radius");
        Check(MaintenanceFeatureRecognition.DimensionKind("Volant chaîne rayon") == MaintenanceFeatureDimensionKind.WheelRadius,
            "Accents and chainwheel radius are recognized");
        Check(MaintenanceFeatureRecognition.DimensionKind("Chaine longueur") == null, "An unspecified total chain length is not silently interpreted as hanging drop");

        XYZ wheel = new XYZ(0, -Mm(500), Mm(1200));
        var geometry = new List<MaintenanceFeatureObservation> {
            Circle(wheel, -XYZ.BasisY, Mm(200), "réducteur à volant", true, Mm(30), true),
            Circle(wheel + XYZ.BasisY * Mm(15), XYZ.BasisY, Mm(200), "réducteur à volant", true),
            Circle(wheel - XYZ.BasisY * Mm(15), -XYZ.BasisY, Mm(200), "réducteur à volant", true),
            Circle(new XYZ(0, 0, Mm(1100)), XYZ.BasisZ, Mm(45), "réducteur à volant", true, Mm(10), true),
            Circle(new XYZ(0, 0, Mm(1105)), XYZ.BasisZ, Mm(45), "réducteur à volant", true)
        };
        var suggestion = Recognize(MaintenanceAccessOperation.HandwheelValve, geometry, Dimension(MaintenanceFeatureDimensionKind.WheelRadius, Mm(200)));
        Check(suggestion.HasCommandPoint && (suggestion.CommandPoint - wheel).GetLength() < Mm(1), "Real wheel is selected instead of the gearbox hub");
        Check(suggestion.Axis.DotProduct(-XYZ.BasisY) > .999, "Wheel axis points away from the connected body");
        Check(suggestion.Confidence == MaintenanceFeatureConfidence.High && !suggestion.Manufacturer, "Parameter/geometry agreement is explicit and is not a manufacturer notice");
        Check(suggestion.Frame.Origin == suggestion.CommandPoint && suggestion.Frame.BasisZ.DotProduct(suggestion.Axis) > .999, "Suggested frame uses world command point and axis");
        foreach (double nominal in new[] { 150.0, 155.0 })
        {
            double section = nominal == 150 ? 19 : 20;
            var rim = new[] { Circle(wheel, -XYZ.BasisY, Mm(nominal + section / 2), "réducteur à volant", true, Mm(section), true),
                Circle(wheel + XYZ.BasisY * Mm(section / 2), -XYZ.BasisY, Mm(nominal + section / 2), "réducteur à volant", true) };
            var measured = Recognize(MaintenanceAccessOperation.HandwheelValve, rim, Dimension(MaintenanceFeatureDimensionKind.WheelRadius, Mm(nominal)));
            Check(measured.HasRadius && Math.Abs(measured.Radius - Mm(nominal)) < Mm(.01), "Principal family radius is the rim centreline, not its outside");
            Check(measured.HasHandleThickness && Math.Abs(measured.HandleThickness - Mm(section)) < Mm(.01), "Outer-minus-nominal radius recovers the tube section diameter");
            Check(Math.Abs(measured.Radius + measured.HandleThickness / 2 - Mm(nominal + section / 2)) < Mm(.01),
                "Represented wheel outer radius equals observed outer radius without double expansion");
        }
        var revolved = Circle(wheel, -XYZ.BasisY, Mm(165), "réducteur à volant", true, Mm(20));
        revolved.CenterlineRadius = Mm(155); revolved.RimThickness = Mm(20);
        var torus = Recognize(MaintenanceAccessOperation.HandwheelValve, new[] { revolved });
        Check(Math.Abs(torus.Radius - Mm(155)) < Mm(.01) && Math.Abs(torus.HandleThickness - Mm(20)) < Mm(.01),
            "A measured revolved annular section can recover centreline/thickness without named dimensions");
        var outerOnly = Recognize(MaintenanceAccessOperation.HandwheelValve, new[] { Circle(wheel, -XYZ.BasisY, Mm(200), "réducteur à volant", true) });
        Check(Math.Abs(outerOnly.HandleThickness - Mm(2)) < Mm(.01) && outerOnly.Warnings.Any(w => w.Contains("Rayon extérieur")),
            "Unresolved outer radius uses an explicit thin trace instead of another default 35mm tube");

        var flange = new[] { Circle(Connectors()[0].Center, XYZ.BasisX, Mm(200), "famille appelée volant", false, Mm(15), true),
            Circle(Connectors()[0].Center + XYZ.BasisX * Mm(7), XYZ.BasisX, Mm(200), "famille appelée volant") };
        var wrong = Recognize(MaintenanceAccessOperation.HandwheelValve, flange, Dimension(MaintenanceFeatureDimensionKind.WheelRadius, Mm(200)));
        Check(!wrong.HasCommandPoint, "A flange on the piping connector is not promoted to a wheel by a name or matching radius");
        var ambiguousGeometry = geometry.Concat(geometry.Take(3).Select(o => Circle(o.Center + XYZ.BasisX * Mm(800), o.Axis, o.Radius, o.Component, true, o.AxialLength, o.Cylinder))).ToArray();
        var ambiguous = Recognize(MaintenanceAccessOperation.HandwheelValve, ambiguousGeometry, Dimension(MaintenanceFeatureDimensionKind.WheelRadius, Mm(200)));
        Check(ambiguous.Ambiguous && !ambiguous.HasCommandPoint && ambiguous.Candidates.Count >= 2, "Equivalent wheels remain manual candidates");

        XYZ highWheel = new XYZ(0, -Mm(400), Mm(3200));
        var chain = new List<MaintenanceFeatureObservation> {
            Circle(highWheel, -XYZ.BasisY, Mm(150), "réducteur à chaine", true, Mm(25), true),
            Circle(highWheel + XYZ.BasisY * Mm(10), XYZ.BasisY, Mm(150), "réducteur à chaine", true),
            new MaintenanceFeatureObservation { Center = highWheel + new XYZ(-Mm(137.5), 0, -Mm(1250)), Axis = XYZ.BasisZ, Linear = true, AxialLength = Mm(2500), Component = "brin gauche" },
            new MaintenanceFeatureObservation { Center = highWheel + new XYZ(Mm(137.5), 0, -Mm(1250)), Axis = XYZ.BasisZ, Linear = true, AxialLength = Mm(2500), Component = "brin droit" }
        };
        var chainResult = Recognize(MaintenanceAccessOperation.ChainwheelValve, chain, Dimension(MaintenanceFeatureDimensionKind.WheelRadius, Mm(150)));
        Check(chainResult.HasCommandPoint && Math.Abs(chainResult.CommandPoint.Z - highWheel.Z) < Mm(5), "Chain command is the high wheel, never the middle of the dangling chain");
        Check(chainResult.HasChain && Math.Abs(chainResult.ChainDrop - Mm(2500)) < Mm(.1) && Math.Abs(chainResult.ChainSpacing - Mm(275)) < Mm(.1),
            "Two actual vertical branches determine drop and spacing");
        var outlinedBranches = chain.Take(2).Concat(new[] { -148.5, 148.5, -126.5, 126.5 }.Select(x => new MaintenanceFeatureObservation {
            Center = highWheel + new XYZ(Mm(x),0,-Mm(1250)), Axis = XYZ.BasisZ, Linear = true, AxialLength = Mm(2500), Component = "contour du brin" })).ToArray();
        var outlinedChain = Recognize(MaintenanceAccessOperation.ChainwheelValve, outlinedBranches, Dimension(MaintenanceFeatureDimensionKind.WheelRadius, Mm(150)));
        Check(Math.Abs(outlinedChain.ChainSpacing - Mm(275)) < Mm(.01), "Outline edges at 297mm outside spacing are regrouped to 275mm branch centrelines");
        var innerRadiusBranches = chain.Take(2).Concat(new[] { -159.5, 159.5, -137.5, 137.5 }.Select(x => new MaintenanceFeatureObservation {
            Center = highWheel + new XYZ(Mm(x),0,-Mm(1250)), Axis = XYZ.BasisZ, Linear = true, AxialLength = Mm(2500), Component = "contour de chaîne avec rayon intérieur137.5" })).ToArray();
        var declaredInner = Recognize(MaintenanceAccessOperation.ChainwheelValve, innerRadiusBranches,
            Dimension(MaintenanceFeatureDimensionKind.WheelRadius, Mm(150)), Dimension(MaintenanceFeatureDimensionKind.ChainRadius, Mm(137.5)));
        Check(Math.Abs(declaredInner.ChainSpacing - Mm(297)) < Mm(.01), "An inner radius137.5 and outer radius159.5 correctly yield297 centre spacing, never275");
        Check(declaredInner.Evidence.Any(e => e.Contains("rayon intérieur")) && declaredInner.Evidence.Any(e => e.Contains("Sections des brins")),
            "Measured cross-section limits distinguish inner radius from branch centreline explicitly");
        var chainDimension = Dimension(MaintenanceFeatureDimensionKind.WheelRadius, Mm(150)); chainDimension.Name = "Volant chaine rayon";
        var automaticChain = MaintenanceFeatureRecognition.Infer(MaintenanceAccessOperation.HandwheelValve, chain, new[] { chainDimension }, Connectors());
        Check(automaticChain.Operation == MaintenanceAccessOperation.ChainwheelValve && automaticChain.HasCommandPoint, "A valve entry automatically selects the geometrically supported chain operation");
        XYZ loopCenter = highWheel - XYZ.BasisZ * Mm(2500);
        chain.Add(new MaintenanceFeatureObservation { Center = loopCenter, Axis = -XYZ.BasisY, Radius = Mm(137.5), Coverage = Math.PI,
            CurvePoints = Enumerable.Range(0, 49).Select(i => loopCenter + new XYZ(Mm(137.5) * Math.Cos(i * Math.PI / 48), 0, -Mm(137.5) * Math.Sin(i * Math.PI / 48))).ToArray() });
        chainResult = Recognize(MaintenanceAccessOperation.ChainwheelValve, chain, Dimension(MaintenanceFeatureDimensionKind.WheelRadius, Mm(150)));
        Check(Math.Abs(chainResult.ChainDrop - Mm(2637.5)) < Mm(.1), "The lower chain loop contributes to the detected drop");
        var lone = Recognize(MaintenanceAccessOperation.ChainwheelValve, geometry.Take(3).Select(o => Circle(o.Center, o.Axis, o.Radius)));
        Check(!lone.HasCommandPoint, "A circular object without chain/function/parameter evidence remains manual");

        XYZ branch = new XYZ(1, 0, -1).Normalize();
        var filter = new[] { Circle(branch * Mm(300), branch, Mm(120), "filtre", false, Mm(20), true),
            Circle(branch * Mm(310), branch, Mm(120), "filtre"), Circle(branch * Mm(290), -branch, Mm(120), "filtre"),
            Circle(branch * Mm(140), branch, Mm(112), "corps de filtre", false, Mm(260), true),
            Circle(branch * Mm(270), branch, Mm(112), "corps de filtre"),
            Circle(new XYZ(Mm(250), 0, 0), XYZ.BasisX, Mm(170), "bride", false, Mm(20), true) };
        var cap = Recognize(MaintenanceAccessOperation.Filter, filter);
        Check(cap.HasCommandPoint && (cap.CommandPoint - branch * Mm(310)).GetLength() < Mm(2), "An external terminal cap is chosen instead of connector flanges or internal collar");
        Check(cap.Axis.DotProduct(branch) > .999 && !cap.DirectionAmbiguous, "Cap removal points outward along its physical branch");
        Check(!cap.HasRemoval && !cap.HasRemovalTravel, "A visible cap cannot invent the internal cartridge or maintenance stroke");
        var withPart = Recognize(MaintenanceAccessOperation.Filter, filter, Dimension(MaintenanceFeatureDimensionKind.CartridgeDiameter, Mm(200)),
            Dimension(MaintenanceFeatureDimensionKind.CartridgeLength, Mm(450)), Dimension(MaintenanceFeatureDimensionKind.ExtractionTravel, Mm(550)));
        Check(withPart.HasRemoval && withPart.HasRemovalTravel && Math.Abs(withPart.RemovalCenterOffset.Z + Mm(225)) < Mm(.1),
            "Only explicit cartridge and maintenance parameters populate a removal volume/stroke");

        // Exact local measurements from native Antibes filter 649237 inspection.
        XYZ a = new XYZ(-.7071067811865469, 0, .7071067811865481);
        var nativeCap = new[] {
            Circle(new XYZ(.3731747101181846,0,-.8653006942444006), -a, .39862204724409445, "CML_Filtre / cap form"),
            Circle(new XYZ(.27003678322600017,0,-.762162767352216), a, .39862204724409445, "CML_Filtre / cap form"),
            Circle(new XYZ(.27003678322600044,0,-.7621627673522163), -a, .400262467191601, "CML_Filtre / collar"),
            Circle(new XYZ(.15684952549491987,0,-.6489755096211356), a, .400262467191601, "CML_Filtre / collar"),
            Circle(new XYZ(.1568495254949202,0,-.648975509621136), -a, .3674540682414698, "CML_Filtre / branch"),
            Circle(new XYZ(-.4027354167218294,0,-.08939056740438567), a, .3674540682414698, "CML_Filtre / branch"),
            Circle(new XYZ(.94,0,0), XYZ.BasisX, .5577427821522309, "CML_Filtre / flange"),
            Circle(new XYZ(-.94,0,0), XYZ.BasisX, .5577427821522309, "CML_Filtre / flange") };
        var native = Recognize(MaintenanceAccessOperation.Filter, nativeCap);
        Check(native.HasCommandPoint && (native.CommandPoint - nativeCap[0].Center).GetLength() < Mm(2), "Recorded native Antibes cap is identified at its outer face");
        // Exact local cap/junction from Antibes filter1014886 (diagnostic-v5).
        // Model construction circles at the hydraulic intersection previously
        // gained a terminal-cap bonus in the arbitrary inward axis direction.
        XYZ actualCover = new XYZ(.3155225117132261,0,-.709223299014198);
        XYZ innerCover = new XYZ(.22474356299881038,0,-.6184443502997965);
        XYZ junction = new XYZ(-.3937007873009719,0,0), inward = new XYZ(-1,0,1).Normalize();
        var fixture1014886 = new[] { Circle((actualCover+innerCover)/2,-inward,Mm(94),"corps PN16",false,(actualCover-innerCover).GetLength(),true),
            Circle(actualCover,-inward,Mm(94),"corps PN16"),Circle(innerCover,inward,Mm(94),"corps PN16"),
            Circle(junction,inward,Mm(84.5),"courbe modèle"),Circle(junction,inward,Mm(84.5),"courbe modèle") };
        var exactConnectors = new[] {new MaintenanceFeatureConnector {Center=new XYZ(-.7874015748031553,0,0),Axis=-XYZ.BasisX,Radius=Mm(75)},
            new MaintenanceFeatureConnector {Center=new XYZ(.7874015748031553,0,0),Axis=XYZ.BasisX,Radius=Mm(75)}};
        foreach(bool placed in new[] {false,true})
        {
            var orientation=Transform.Identity;
            if(placed){orientation.Origin=new XYZ(66.55868734461055,202.48548866177947,121.61219734075108);orientation.BasisX=-XYZ.BasisZ;
                orientation.BasisY=-XYZ.BasisY;orientation.BasisZ=-XYZ.BasisX;}
            var worldGeometry=fixture1014886.Select(o=>Circle(orientation.OfPoint(o.Center),orientation.OfVector(o.Axis),o.Radius,o.Component,o.ComponentExplicit,o.AxialLength,o.Cylinder)).ToArray();
            var worldConnectors=exactConnectors.Select(c=>new MaintenanceFeatureConnector {Center=orientation.OfPoint(c.Center),Axis=orientation.OfVector(c.Axis),Radius=c.Radius}).ToArray();
            var actual=MaintenanceFeatureRecognition.Recognize(MaintenanceAccessOperation.Filter,worldGeometry,Array.Empty<MaintenanceFeatureDimension>(),worldConnectors);
            Check(actual.HasCommandPoint&&!actual.Ambiguous&&(actual.CommandPoint-orientation.OfPoint(actualCover)).GetLength()<Mm(.1),
                "Antibes1014886 external cover is proposed despite inward construction circles at the junction");
            Check(actual.Confidence==MaintenanceFeatureConfidence.Medium&&!actual.Manufacturer&&!actual.HasRemoval&&!actual.HasRemovalTravel,
                "The plausible cover retains explicit confidence without certifying internal maintenance dimensions");
            var construction=actual.Candidates.First(c=>c.Label=="courbe modèle");
            Check(construction.Score<actual.Candidates[0].Score-3&&construction.Evidence.Any(e=>e.Contains("jonction hydraulique")),
                "An internal construction circle is retained as geometry but cannot create false terminal-cap ambiguity");
        }
        var opposedCover=fixture1014886.Take(3).Select(o=>Circle(new XYZ(o.Center.X,o.Center.Y,-o.Center.Z),new XYZ(o.Axis.X,o.Axis.Y,-o.Axis.Z),o.Radius,"autre capot",false,o.AxialLength,o.Cylinder))
            .Concat(new[] {Circle(junction,new XYZ(-1,0,-1).Normalize(),Mm(84.5),"autre jonction")});
        var realAmbiguity=MaintenanceFeatureRecognition.Recognize(MaintenanceAccessOperation.Filter,fixture1014886.Concat(opposedCover),Array.Empty<MaintenanceFeatureDimension>(),exactConnectors);
        Check(realAmbiguity.Ambiguous&&!realAmbiguity.HasCommandPoint,"Two genuine external closures still require an explicit user choice");
        var mount = Transform.Identity; mount.Origin = new XYZ(Mm(30), Mm(-50), Mm(500));
        XYZ[] housing = Enumerable.Range(0, 8).Select(i => mount.OfPoint(new XYZ((i & 1) == 0 ? -Mm(120) : Mm(150),
            (i & 2) == 0 ? -Mm(130) : Mm(180), (i & 4) == 0 ? 0 : Mm(400)))).ToArray();
        var actuatorPart = Circle(mount.Origin, XYZ.BasisZ, Mm(10), "Actionneur électrique PSQ", true);
        actuatorPart.ComponentFrame = mount; actuatorPart.ComponentPoints = housing.Concat(housing).ToArray();
        var motorized = MaintenanceFeatureRecognition.Infer(MaintenanceAccessOperation.HandwheelValve, new[] { actuatorPart }, Array.Empty<MaintenanceFeatureDimension>(), Connectors());
        Check(motorized.Operation == MaintenanceAccessOperation.HandwheelValve && !motorized.HasRemoval && !motorized.HasCommandPoint,
            "A motorized valve cannot replace a requested manual operation with removal");
        Check(motorized.Warnings.Any(w=>w.Contains("Commande motorisée reconnue")),"Motor-only manual operation explains the missing real manual control");
        var removal=MaintenanceFeatureRecognition.Infer(MaintenanceAccessOperation.Actuator,new[]{actuatorPart},Array.Empty<MaintenanceFeatureDimension>(),Connectors());
        Check(removal.Operation==MaintenanceAccessOperation.Actuator&&removal.HasRemoval&&removal.HasCommandPoint,"Explicit actuator removal uses the measured mounting frame and motor volume");
        Check((removal.CommandPoint-mount.Origin).GetLength()<Mm(.01),"Removal pivot stays on the actual motor mounting face");
        Check(!removal.HasRemovalTravel&&!removal.Manufacturer,"An isolated actuator does not invent travel or manufacturer approval");
        var directRemoval=Recognize(MaintenanceAccessOperation.Actuator,new[]{actuatorPart});
        Check(directRemoval.HasRemoval&&directRemoval.HasCommandPoint,"Direct actuator recognition has the same strict removal contract");
        var wrongRemoval=Recognize(MaintenanceAccessOperation.Actuator,geometry);
        Check(wrongRemoval.Operation==MaintenanceAccessOperation.Actuator&&!wrongRemoval.HasCommandPoint&&!wrongRemoval.HasRemoval,"An explicit removal request cannot turn a real handwheel into an actuator");
        var capRemoval=Recognize(MaintenanceAccessOperation.Actuator,fixture1014886);
        Check(!capRemoval.HasCommandPoint&&!capRemoval.HasRemoval,"A filter cover cannot stand in for a missing actuator");
        var manualWheel=MaintenanceFeatureRecognition.Infer(MaintenanceAccessOperation.HandwheelValve,geometry.Concat(new[]{actuatorPart}),new[]{Dimension(MaintenanceFeatureDimensionKind.WheelRadius,Mm(200))},Connectors());
        Check(manualWheel.Operation==MaintenanceAccessOperation.HandwheelValve&&manualWheel.HasCommandPoint&&!manualWheel.HasRemoval,"A separate real handwheel remains usable alongside an actuator");
        Check((manualWheel.CommandPoint-wheel).GetLength()<Mm(1),"Manual override uses the wheel centre rather than the actuator mounting frame");
        var motorCircle=Circle(mount.Origin,XYZ.BasisZ,Mm(120),"Actionneur électrique PSQ",true,Mm(20));
        motorCircle.ComponentFrame=mount;motorCircle.ComponentPoints=actuatorPart.ComponentPoints;
        var flattenedCircle=Circle(mount.Origin,XYZ.BasisZ,Mm(120),"vanne motorisée",false,Mm(20));
        var motorFaces=MaintenanceFeatureRecognition.Infer(MaintenanceAccessOperation.HandwheelValve,new[]{motorCircle,flattenedCircle},new[]{Dimension(MaintenanceFeatureDimensionKind.WheelRadius,Mm(120))},Connectors());
        Check(!motorFaces.HasCommandPoint&&motorFaces.Candidates.Count==0,"Motor circular faces and their flattened duplicates cannot become a fictitious wheel through matching parameters");
        var leverParameter = Dimension(MaintenanceFeatureDimensionKind.LeverLength, Mm(155));
        var handle = MaintenanceFeatureRecognition.Infer(MaintenanceAccessOperation.HandwheelValve, new[] { actuatorPart }, new[] { leverParameter }, Connectors());
        Check(handle.Operation!=MaintenanceAccessOperation.Actuator&&!handle.HasRemoval&&!handle.HasCommandPoint,"A possible auxiliary handle length does not select motor removal or invent a manual pivot");
        actuatorPart.Component = "corps avec poignée";
        handle = MaintenanceFeatureRecognition.Infer(MaintenanceAccessOperation.HandwheelValve, new[] { actuatorPart }, new[] { leverParameter }, Connectors());
        Check(handle.Operation == MaintenanceAccessOperation.LeverValve && handle.HasRadius && !handle.HasCommandPoint,
            "A dedicated lever length proposes a lever while preserving manual pivot confirmation");
        ReplayNativeFilter();
        ReplayNativeShortChain();
        ReplayNativeLever();
        return _checks;
    }

    private static void ReplayNativeFilter()
    {
        using(var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","filter-1014886.json"))))
        {
            var root=json.RootElement;
            var labels=root.GetProperty("components").EnumerateArray().Select(v=>v.GetString()).ToArray();
            XYZ Vector(JsonElement array)=>new XYZ(array[0].GetDouble(),array[1].GetDouble(),array[2].GetDouble());
            var observations=root.GetProperty("features").EnumerateArray().Select(row=>new MaintenanceFeatureObservation {
                Center=new XYZ(row[0].GetDouble(),row[1].GetDouble(),row[2].GetDouble()),Axis=new XYZ(row[3].GetDouble(),row[4].GetDouble(),row[5].GetDouble()),
                Radius=row[6].GetDouble(),AxialLength=row[7].GetDouble(),Coverage=row[8].GetDouble(),Component=labels[row[9].GetInt32()],
                Cylinder=row[10].GetInt32()!=0,ComponentExplicit=row[11].GetInt32()!=0,Origin="native diagnostic-v5"
            }).ToArray();
            var connectors=root.GetProperty("connectors").EnumerateArray().Select(c=>new MaintenanceFeatureConnector {
                Center=Vector(c.GetProperty("origin")),Axis=Vector(c.GetProperty("normal")),Radius=c.GetProperty("radius").GetDouble()
            }).ToArray();
            var actual=MaintenanceFeatureRecognition.Recognize(MaintenanceAccessOperation.Filter,observations,Array.Empty<MaintenanceFeatureDimension>(),connectors);
            Check(actual.HasCommandPoint&&!actual.Ambiguous,"All native circular observations for filter1014886 produce an external cap suggestion");
            Check((actual.CommandPoint-Vector(root.GetProperty("expectedCommand"))).GetLength()<Mm(.1)
                &&actual.Axis.DotProduct(Vector(root.GetProperty("expectedAxis")))>.9999,"Native replay keeps the measured cap centre and outward axis");
            Check(actual.Confidence==MaintenanceFeatureConfidence.Medium&&!actual.Manufacturer&&!actual.HasRemoval&&!actual.HasRemovalTravel,
                "Native replay cannot turn a cap observation into certified cartridge dimensions");
        }
    }
    private static void ReplayNativeShortChain()
    {
        using(var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","chain-1045267.json"))))
        {
            var root=json.RootElement;
            XYZ Vector(JsonElement array)=>new XYZ(array[0].GetDouble(),array[1].GetDouble(),array[2].GetDouble());
            var geometry=root.GetProperty("features").EnumerateArray().Select(o=>new MaintenanceFeatureObservation {
                Center=Vector(o.GetProperty("center")),Axis=Vector(o.GetProperty("axis")),Radius=o.GetProperty("radius").GetDouble(),
                AxialLength=o.GetProperty("axialLength").GetDouble(),Coverage=o.GetProperty("coverage").GetDouble(),
                Cylinder=o.GetProperty("cylinder").GetBoolean(),Linear=o.GetProperty("linear").GetBoolean(),ComponentExplicit=o.GetProperty("explicitComponent").GetBoolean(),
                Component=o.GetProperty("component").GetString(),Origin="native Antibes1045267",
                CenterlineRadius=o.GetProperty("centerlineRadius").GetDouble(),RimThickness=o.GetProperty("rimThickness").GetDouble(),
                CurvePoints=o.GetProperty("curvePoints").EnumerateArray().Select(Vector).ToArray()
            }).ToArray();
            var dimensions=root.GetProperty("dimensions").EnumerateArray().Select(d=>new MaintenanceFeatureDimension {
                Kind=Enum.Parse<MaintenanceFeatureDimensionKind>(d.GetProperty("kind").GetString()),Name=d.GetProperty("name").GetString(),
                Component=d.GetProperty("component").GetString(),Origin=d.GetProperty("origin").GetString(),Value=d.GetProperty("value").GetDouble()
            }).ToArray();
            var connectors=root.GetProperty("connectors").EnumerateArray().Select(c=>new MaintenanceFeatureConnector {
                Center=Vector(c.GetProperty("world")),Axis=Vector(c.GetProperty("axis")),Radius=c.GetProperty("radius").GetDouble()
            }).ToArray();
            foreach(var ordered in new[] {geometry,geometry.Reverse().ToArray()})
            {
                var result=MaintenanceFeatureRecognition.Infer(MaintenanceAccessOperation.HandwheelValve,ordered,dimensions,connectors);
                Check(result.Operation==MaintenanceAccessOperation.ChainwheelValve&&result.HasCommandPoint&&!result.Ambiguous,
                    "Native1045267 retains its high wheel and operation regardless of geometry enumeration order");
                Check((result.CommandPoint-Vector(root.GetProperty("expectedCommand"))).GetLength()<Mm(.1)&&Math.Abs(result.Radius-Mm(111))<Mm(.01),
                    "Native1045267 command centre/rim agree with the recorded wheel geometry");
                Check(result.HasChain&&Math.Abs(result.ChainDrop-Mm(root.GetProperty("expectedChainDropMm").GetDouble()))<Mm(.1)
                    &&Math.Abs(result.ChainSpacing-Mm(root.GetProperty("expectedChainSpacingMm").GetDouble()))<Mm(.1),
                    "Native1045267 propagates its1000mm brins and120.5mm lower loop instead of default2000/400mm");
                Check(!result.Warnings.Any(w=>w.Contains("chaîne complète n’est pas identifiable")),
                    "A measured short chain cannot be reported as unidentified after candidate scoring proved both branches");
            }
        }
    }
    private static void ReplayNativeLever()
    {
        using(var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","lever-1016925.json"))))
        {
            var root=json.RootElement;
            XYZ Vector(JsonElement array)=>new XYZ(array[0].GetDouble(),array[1].GetDouble(),array[2].GetDouble());
            var geometry=new List<MaintenanceFeatureObservation>();
            foreach(var stem in root.GetProperty("stems").EnumerateArray())
                geometry.Add(new MaintenanceFeatureObservation {Center=Vector(stem.GetProperty("center")),Axis=Vector(stem.GetProperty("axis")),
                    Radius=stem.GetProperty("radius").GetDouble(),AxialLength=stem.GetProperty("length").GetDouble(),Coverage=stem.GetProperty("coverage").GetDouble(),
                    Cylinder=true,Component="visible stem",ProjectVisible=true});
            foreach(var part in root.GetProperty("parts").EnumerateArray())
            {
                var points=part.GetProperty("points").EnumerateArray().Select(Vector).ToArray();
                geometry.Add(new MaintenanceFeatureObservation {Center=points.Aggregate(XYZ.Zero,(a,b)=>a+b)/points.Length,Axis=XYZ.BasisZ,
                    Component=part.GetProperty("label").GetString(),ComponentPoints=points,ProjectVisible=part.GetProperty("visible").GetBoolean()});
            }
            var ports=root.GetProperty("connectors").EnumerateArray().Select(c=>new MaintenanceFeatureConnector {
                Center=Vector(c.GetProperty("origin")),Axis=Vector(c.GetProperty("normal")),Radius=c.GetProperty("radius").GetDouble()}).ToArray();
            var transforms=new[] {Transform.Identity,Transform.CreateRotation(XYZ.BasisZ,.73),Transform.CreateRotation(new XYZ(1,2,3).Normalize(),1.13),
                new Transform {BasisX=-XYZ.BasisX},new Transform {BasisZ=-XYZ.BasisZ}};
            foreach(var transform in transforms)
            foreach(double scale in new[] {.4,1.0,2.0})
            {
                transform.Origin=new XYZ(120,-31,45);
                var mapped=geometry.Select((o,i)=>new MaintenanceFeatureObservation {Center=transform.OfPoint(o.Center*scale),Axis=transform.OfVector(o.Axis),
                    Radius=o.Radius*scale,AxialLength=o.AxialLength*scale,Coverage=o.Coverage,Cylinder=o.Cylinder,ProjectVisible=o.ProjectVisible,
                    Component="anonymous "+i,ComponentPoints=o.ComponentPoints?.Select(p=>transform.OfPoint(p*scale)).ToArray()}).Reverse().ToArray();
                var mappedPorts=ports.Select(c=>new MaintenanceFeatureConnector {Center=transform.OfPoint(c.Center*scale),Axis=transform.OfVector(c.Axis),Radius=c.Radius*scale}).ToArray();
                var detected=MaintenanceFeatureRecognition.Infer(MaintenanceAccessOperation.HandwheelValve,mapped,Array.Empty<MaintenanceFeatureDimension>(),mappedPorts);
                Check(detected.Operation==MaintenanceAccessOperation.LeverValve&&detected.HasCommandPoint&&!detected.Ambiguous,
                    "A rotated/mirrored/renamed scaled native handle is detected without any dimension parameter or family-name exception");
                Check((detected.CommandPoint-transform.OfPoint(Vector(root.GetProperty("expectedPivot"))*scale)).GetLength()<Mm(.2)
                    &&detected.Frame.BasisX.DotProduct(transform.OfVector(XYZ.BasisX))>.9999&&detected.Axis.DotProduct(transform.OfVector(XYZ.BasisZ))>.9999,
                    "The native lever pivot, stem axis and initial direction follow the placed geometry including mirrored placements");
                Check(Math.Abs(detected.Radius-Mm(155)*scale)<Mm(.2)&&Math.Abs(detected.HandleThickness-Mm(46)*scale)<Mm(.2),
                    "Lever reach and conservative square section include the real bent handle instead of an arbitrary family bounding box");
                Check(detected.HasRotation&&detected.RotationDegrees==90&&detected.InitialAngleDegrees==0&&detected.RotationAssumed&&!detected.Manufacturer,
                    "Initial direction is measured but the90-degree travel remains an explicit editable hypothesis");
                Check(detected.Frame.BasisX.CrossProduct(detected.Frame.BasisY).DotProduct(detected.Frame.BasisZ)>.9999,
                    "A reflected source still yields a valid right-handed operation frame without pretending the rotation sense is known");
            }
            var native=MaintenanceFeatureRecognition.Recognize(MaintenanceAccessOperation.LeverValve,geometry,Array.Empty<MaintenanceFeatureDimension>(),ports);
            Check(native.HasCommandPoint&&native.Candidates.Count==1,"Direct lever recognition uses only the visible open handle, not the hidden closed alternative");
            var closed=geometry.Select(o=>new MaintenanceFeatureObservation {Center=o.Center,Axis=o.Axis,Radius=o.Radius,AxialLength=o.AxialLength,Coverage=o.Coverage,Cylinder=o.Cylinder,
                Component=o.Component,ComponentPoints=o.ComponentPoints,ProjectVisible=true}).ToArray();
            var ambiguous=MaintenanceFeatureRecognition.Recognize(MaintenanceAccessOperation.LeverValve,closed,Array.Empty<MaintenanceFeatureDimension>(),ports);
            Check(ambiguous.Ambiguous&&!ambiguous.HasCommandPoint&&ambiguous.Candidates.Count==2&&ambiguous.Candidates.All(c=>c.Frame!=null),
                "Two genuinely visible handle positions remain distinct oriented candidates requiring user choice");
            var withoutProject=closed.Select(o=>new MaintenanceFeatureObservation {Center=o.Center,Axis=o.Axis,Radius=o.Radius,AxialLength=o.AxialLength,Coverage=o.Coverage,Cylinder=o.Cylinder,
                Component=o.Component,ComponentPoints=o.ComponentPoints}).ToArray();
            var unknownVisibility=MaintenanceFeatureRecognition.Recognize(MaintenanceAccessOperation.LeverValve,withoutProject,Array.Empty<MaintenanceFeatureDimension>(),ports);
            Check(unknownVisibility.Ambiguous&&!unknownVisibility.HasCommandPoint,"Family-only geometry never silently chooses between open and closed handle solids");
            var arm=geometry.Single(o=>o.ProjectVisible&&o.ComponentPoints!=null);
            var duplicates=geometry.Concat(new[] {new MaintenanceFeatureObservation {Center=arm.Center,Axis=arm.Axis,ComponentPoints=arm.ComponentPoints.Select(p=>new XYZ(p.X,p.Y,p.Z)).ToArray(),ProjectVisible=true}});
            Check(!MaintenanceFeatureRecognition.Recognize(MaintenanceAccessOperation.LeverValve,duplicates,Array.Empty<MaintenanceFeatureDimension>(),ports).Ambiguous,
                "Duplicate project/shared geometry cannot create a false second handle candidate");
            var detached=geometry.Select(o=>new MaintenanceFeatureObservation {Center=o.Center,Axis=o.Axis,Radius=o.Radius,AxialLength=o.AxialLength,Coverage=o.Coverage,Cylinder=o.Cylinder,
                Component=o.Component,ComponentPoints=o.ComponentPoints?.Select(p=>p+XYZ.BasisZ*Mm(80)).ToArray(),ProjectVisible=o.ProjectVisible}).ToArray();
            Check(!MaintenanceFeatureRecognition.Recognize(MaintenanceAccessOperation.LeverValve,detached,Array.Empty<MaintenanceFeatureDimension>(),ports).HasCommandPoint,
                "An elongated piece detached from the stem cannot become a lever because its bounds look suitable");
            Check(!MaintenanceFeatureRecognition.Recognize(MaintenanceAccessOperation.LeverValve,geometry.Where(o=>o.Cylinder),Array.Empty<MaintenanceFeatureDimension>(),ports).HasCommandPoint,
                "A valve stem or plumbing cylinder without an operating arm is insufficient");
            var cap=Circle(new XYZ(0,0,Mm(200)),XYZ.BasisZ,Mm(100),"capot",true,Mm(20),true);
            Check(!MaintenanceFeatureRecognition.Recognize(MaintenanceAccessOperation.LeverValve,new[] {cap},Array.Empty<MaintenanceFeatureDimension>(),ports).HasCommandPoint,
                "The dedicated lever path cannot classify an external circular cover as a lever");
            var plumbing=new[] {Circle(ports[0].Center,XYZ.BasisX,Mm(45),"flange",true,Mm(12),true),Circle(ports[1].Center,-XYZ.BasisX,Mm(45),"flange",true,Mm(12),true)};
            Check(!MaintenanceFeatureRecognition.Recognize(MaintenanceAccessOperation.LeverValve,plumbing,Array.Empty<MaintenanceFeatureDimension>(),ports).HasCommandPoint,
                "Connector-aligned flanges remain plumbing, even in explicit lever mode");
            Check(!MaintenanceFeatureRecognition.Recognize(MaintenanceAccessOperation.LeverValve,geometry,Array.Empty<MaintenanceFeatureDimension>(),Array.Empty<MaintenanceFeatureConnector>()).HasCommandPoint,
                "Missing piping reference geometry leaves the lever pivot manual");
            Check(MaintenanceFeatureRecognition.DimensionKind("Angle poignée")!=MaintenanceFeatureDimensionKind.LeverRotation
                &&MaintenanceFeatureRecognition.DimensionKind("Course angulaire du levier")==MaintenanceFeatureDimensionKind.LeverRotation,
                "A current angle is not silently converted into a mechanical angular travel");
            var course=new MaintenanceFeatureDimension {Kind=MaintenanceFeatureDimensionKind.LeverRotation,Value=60,Name="Course angulaire du levier",Origin="model"};
            var declared=MaintenanceFeatureRecognition.Recognize(MaintenanceAccessOperation.LeverValve,geometry,new[] {course},ports);
            Check(declared.HasRotation&&declared.RotationDegrees==60&&!declared.RotationAssumed&&!declared.Manufacturer,
                "An explicit angular travel parameter replaces the default hypothesis without becoming manufacturer certification");
        }
    }
}
