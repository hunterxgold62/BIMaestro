using System;
using System.Linq;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI.Selection;
using Autodesk.Revit.DB.Plumbing;

namespace MyFlangePlugin
{
    [Transaction(TransactionMode.Manual)]
    public class AddFlangesCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;

            try
            {
                // 1) Sélection de l’accessoire
                var picked = uidoc.Selection
                                   .PickObject(ObjectType.Element,
                                               "Sélectionnez un accessoire de canalisation");
                if (picked == null) return Result.Cancelled;

                var accessory = doc.GetElement(picked) as FamilyInstance;
                if (accessory == null)
                {
                    message = "Veuillez sélectionner un FamilyInstance.";
                    return Result.Failed;
                }

                // 2) Charger/activer le symbole de bride
                var flangeSym = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_PipeAccessory)
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(fs =>
                        fs.Family.Name.Equals(
                            "CML_Bride à collerette tous PN",
                            StringComparison.OrdinalIgnoreCase));
                if (flangeSym == null)
                {
                    message = "Symbole 'CML_Bride à collerette tous PN' introuvable.";
                    return Result.Failed;
                }
                if (!flangeSym.IsActive)
                {
                    using (var txA = new Transaction(doc, "Activer bride"))
                    {
                        txA.Start();
                        flangeSym.Activate();
                        txA.Commit();
                    }
                }

                // 3) Récupérer le niveau de l’accessoire
                Level accessoryLevel = doc.GetElement(accessory.LevelId) as Level;
                if (accessoryLevel == null)
                {
                    message = "Niveau introuvable.";
                    return Result.Failed;
                }

                // 4) Récupérer tous les connecteurs End de l’accessoire
                var cm = accessory.MEPModel?.ConnectorManager;
                if (cm == null)
                {
                    message = "Aucun connecteur MEP trouvé.";
                    return Result.Failed;
                }
                var ends = cm.Connectors
                             .Cast<Connector>()
                             .Where(c => c.ConnectorType == ConnectorType.End)
                             .ToList();
                if (ends.Count < 2)
                {
                    message = "Il faut au moins deux connecteurs End.";
                    return Result.Failed;
                }

                // 5) Place et câble les brides
                using (var tx = new Transaction(doc, "Ajouter Brides"))
                {
                    tx.Start();

                    foreach (var endConn in ends)
                    {
                        // A) Déconnecter les tuyaux existants
                        var pipeConns = endConn.AllRefs
                                               .OfType<Connector>()
                                               .Where(rc => rc.Owner is Pipe)
                                               .ToList();

                        // B) Lire le diamètre (paramètre natif ou 'Taille')
                        string tailleValue = "";
                        if (pipeConns.Any())
                        {
                            var hostPipe = (Pipe)pipeConns[0].Owner;
                            var diamParam = hostPipe.get_Parameter(
                                    BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)
                                ?? hostPipe.LookupParameter("Taille");
                            if (diamParam != null)
                                tailleValue = (diamParam.AsValueString()
                                              ?? diamParam.AsString() ?? "")
                                              .Trim();
                        }

                        // Retirer d’abord les connexions existantes
                        foreach (var pc in pipeConns)
                            try { endConn.DisconnectFrom(pc); }
                            catch { }

                        // C) Création + orientation / translation / flip
                        var fiBride = PlaceFlange(
                            doc,
                            endConn,
                            flangeSym,
                            accessoryLevel,
                            pipeConns,
                            tailleValue);
                        if (fiBride == null)
                            continue;

                        // D) Reconnecter : primaire ↔ accessoire, secondaire ↔ tuyaux
                        var bConns = fiBride.MEPModel
                                             .ConnectorManager
                                             .Connectors
                                             .Cast<Connector>()
                                             .Where(c => c.ConnectorType == ConnectorType.End)
                                             .ToList();
                        if (bConns.Count < 2)
                            continue;

                        Connector primary = bConns
                            .FirstOrDefault(bc =>
                                bc.CoordinateSystem.BasisZ
                                  .DotProduct(endConn.CoordinateSystem.BasisZ) < 0)
                            ?? bConns
                                .OrderBy(bc =>
                                    bc.Origin.DistanceTo(endConn.Origin))
                                .First();

                        var secondary = bConns.First(bc => bc.Id != primary.Id);

                        try { endConn.ConnectTo(primary); }
                        catch { }

                        foreach (var pc in pipeConns)
                            try { secondary.ConnectTo(pc); }
                            catch { }
                    }

                    tx.Commit();
                }

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }

        /// <summary>
        /// Crée la bride, la translate pour aligner son connecteur primaire
        /// sur endConn, puis utilise le paramètre "Inverser le raccord"
        /// si besoin, et met à jour DN.
        /// </summary>
        private FamilyInstance PlaceFlange(
            Document doc,
            Connector endConn,
            FamilySymbol flangeSym,
            Level level,
            List<Connector> pipeConns,
            string tailleValue)
        {
            // 1) Création hébergée si possible, sinon sur niveau
            XYZ insertionPt = endConn.Origin;
            FamilyInstance fi;
            var hostPipe = pipeConns.FirstOrDefault()?.Owner as Pipe;

            if (hostPipe != null)
            {
                fi = doc.Create.NewFamilyInstance(
                    insertionPt,
                    flangeSym,
                    hostPipe,
                    StructuralType.NonStructural);
            }
            else
            {
                fi = doc.Create.NewFamilyInstance(
                    insertionPt,
                    flangeSym,
                    level,
                    StructuralType.NonStructural);
            }
            doc.Regenerate();

            // 2) Translation pour recaler le connecteur “primaire”
            var bConns = fi.MEPModel
                           .ConnectorManager
                           .Connectors
                           .Cast<Connector>()
                           .Where(c => c.ConnectorType == ConnectorType.End)
                           .ToList();
            if (bConns.Count >= 2)
            {
                Connector primary = bConns
                    .FirstOrDefault(bc =>
                        bc.CoordinateSystem.BasisZ
                          .DotProduct(endConn.CoordinateSystem.BasisZ) < 0)
                    ?? bConns
                        .OrderBy(bc =>
                            bc.Origin.DistanceTo(endConn.Origin))
                        .First();

                XYZ delta = endConn.Origin - primary.Origin;
                ElementTransformUtils.MoveElement(doc, fi.Id, delta);
                doc.Regenerate();

                // 3) Utilisation du paramètre "Inverser le raccord"
                //    si ce param existe dans la famille
                var invertParam = fi.LookupParameter("Inverser le raccord")
                                  ?? fi.LookupParameter("Inversé le raccord");
                if (invertParam != null && invertParam.StorageType == StorageType.Integer)
                {
                    // Si après translation les normals pointent dans le même sens
                    XYZ movedDir = primary.CoordinateSystem.BasisZ;
                    XYZ accessoryDir = endConn.CoordinateSystem.BasisZ;
                    if (movedDir.DotProduct(accessoryDir) > 0.0)
                    {
                        invertParam.Set(1);
                        doc.Regenerate();
                    }
                    else
                    {
                        invertParam.Set(0);
                        doc.Regenerate();
                    }
                }
                else
                {
                    // Fallback : rotation 180° si pas de param
                    XYZ movedDir = primary.CoordinateSystem.BasisZ;
                    XYZ accessoryDir = endConn.CoordinateSystem.BasisZ;
                    if (movedDir.DotProduct(accessoryDir) > 0.0)
                    {
                        Line flipAxis = Line.CreateBound(endConn.Origin, endConn.Origin + accessoryDir);
                        ElementTransformUtils.RotateElement(doc, fi.Id, flipAxis, Math.PI);
                        doc.Regenerate();
                    }
                }
            }

            // 4) Mise à jour du paramètre DN
            if (!string.IsNullOrEmpty(tailleValue))
            {
                var dnParam = fi.LookupParameter("DN");
                if (dnParam != null && !dnParam.IsReadOnly)
                {
                    if (dnParam.StorageType == StorageType.String)
                    {
                        dnParam.Set(tailleValue);
                    }
                    else if (dnParam.StorageType == StorageType.Double)
                    {
                        var txt = tailleValue
                            .Replace("mm", "")
                            .Replace("DN", "")
                            .Trim();
                        if (double.TryParse(txt, out double mm))
                        {
                            double val = UnitUtils.ConvertToInternalUnits(
                                mm, UnitTypeId.Millimeters);
                            dnParam.Set(val);
                        }
                    }
                }
            }

            return fi;
        }
    }
}
