using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace BIMaestro.Codex
{
    // An API operation graph, not a C#/Python evaluator. Only Revit model objects,
    // scoped documents, typed collections and finite scalar values cross this boundary.
    internal sealed class CodexFamilyProgram
    {
        private readonly UIApplication app;
        private readonly Document doc;
        private readonly Budget budget;
        private readonly int depth;
        private readonly Dictionary<string, object> variables = new Dictionary<string, object>(StringComparer.Ordinal);
        private Transaction transaction;
        private readonly List<string> warnings = new List<string>();
        private sealed class Budget { internal int Steps; }
        private static readonly string[] Reserved = { "doc", "manager", "family", "factory" };
        private CodexFamilyProgram(UIApplication app, Document doc, Budget budget, int depth = 0)
        {
            this.app = app; this.doc = doc; this.budget = budget; this.depth = depth;
            variables["doc"] = doc; variables["manager"] = doc.FamilyManager;
            variables["family"] = doc.OwnerFamily; variables["factory"] = doc.FamilyCreate;
        }
        internal static object Contract() => new {
            version = 1,
            purpose = "Programme général pour créer/modifier une famille ouverte via les objets natifs Revit. Pas de C#, Python, shell, fichiers ou réseau. Lire revit_family_api pour les signatures exactes de cette version.",
            document = "doc, manager, family et factory désignent uniquement la famille courante. Chaque nested possède son propre contexte et renvoie la Family chargée dans son parent. Les objets d'autres documents ne sont pas acceptés.",
            format = "program_json est un objet JSON {steps:[...],outputs:['nom',...]}. Chaque étape a op et un id facultatif. Référence à un résultat : {ref:'nom'}. Littéraux : nombre/booléen/texte/null. Listes : op=list avec type du contenu et items. Noms complets Autodesk.Revit.DB ; types imbriqués au format CLR (+).",
            operations = new[] {
                "new: {op:'new',id:'p',type:'Autodesk.Revit.DB.XYZ',args:[0,0,0]}",
                "call: {op:'call',id:'e',target:{ref:'doc'},member:'GetElement',args:['unique_id_lu']}",
                "static: {op:'static',id:'line',type:'Autodesk.Revit.DB.Line',member:'CreateBound',args:[{ref:'a'},{ref:'b'}]}",
                "get/set: target+member pour une propriété (set utilise value), ou type+member pour une propriété/champ statique. Pas d'indexeur : utiliser call get_Item avec args.",
                "enum: {op:'enum',id:'category',type:'Autodesk.Revit.DB.BuiltInCategory',value:'OST_Furniture'} ; typeof: {op:'typeof',id:'class',type:'Autodesk.Revit.DB.FamilyInstance'} pour OfClass.",
                "list: {op:'list',id:'loops',type:'Autodesk.Revit.DB.CurveLoop',items:[{ref:'loop'}]}. Opérations Add/Clear/Contains/Remove/get_Item/get_Count sur listes.",
                "item: {op:'item',id:'first',target:{ref:'collection'},index:0}. foreach: {op:'foreach',target:{ref:'collection'},var:'entry',steps:[...]} ; au plus 200 itérations. Réutiliser les ids locaux dans le corps, variables du corps non exportées.",
                "mm: {op:'mm',id:'length',value:900} convertit mm en pieds ; xyz_mm: {op:'xyz_mm',id:'p',args:[900,0,750]}. Toutes les autres API utilisent les UNITÉS INTERNES Revit (pieds/radians).",
                "math: {op:'math',id:'x',member:'add|subtract|multiply|divide|min|max|sin|cos|floor|abs|equal|less|greater',args:[...]} ; assert: {op:'assert',value:{ref:'ok'},message:'contrôle attendu'}.",
                "range: {op:'range',id:'indices',args:[0,10,1]} renvoie 10 nombres à partir de 0, pas 1 (200 max). if: {op:'if',value:{ref:'condition'},then:[...],else:[...]} ; else facultatif. math equal accepte aussi deux textes ou deux booléens.",
                "nested création: {op:'nested',id:'module',name:'Service complet',hosting:'free',steps:[...]}. Gabarits hosting free/wall/floor/ceiling/face/work_plane. Famille temporaire, géométrie arbitraire native, chargée sans fichier. Ajouter paramètres et contraintes dans steps. Refuse une famille homonyme.",
                "nested édition: {op:'nested',id:'updated',source_family:{ref:'famille_imbriquée_lue'},overwrite_parameter_values:false,steps:[...]}. Édite la famille imbriquée existante puis recharge sa géométrie dans le parent. false conserve les valeurs de types du parent, true seulement si leur remplacement est demandé. Les deux modes nested sont inclus dans l'annulation du parent."
            },
            overloads = "args est obligatoire pour new/call/static. En cas d'ambiguïté, ajouter signature:[types_exacts_des_paramètres] obtenue par revit_family_api. Les méthodes génériques et paramètres ref/out ne sont pas exécutables. Enums possibles aussi en littéral string si le type attendu est une enum.",
            validation = "Commencer par validate_only=true : toute la famille est ramenée à son état initial, y compris les imports imbriqués. Les IDs de ce test ne sont pas utilisables ensuite. Appliquer ensuite le même programme avec validate_only=false. Erreur/échec d'assertion = annulation complète. Aucun enregistrement/rechargement du projet.",
            limits = "1500 étapes décrites, 10000 exécutées, imbrication 3 niveaux, 200 itérations par boucle, listes de 1000 objets. Courbes bornées >=1 mm et tolérance Revit. Appels synchrones : délai coopératif, pas de garantie d'interruption immédiate du noyau Revit. Pas d'accès fichiers, documents externes, exports, impression, application ou transactions via le graphe.",
            examples = "Verre creux : profil annulaire + NewExtrusion, ou profil de révolution + NewRevolution ; assiette : profil radial + révolution. Service complet : nested contenant ces pièces ; répéter sa FamilyInstance avec LinearArray.Create et FamilyManager pour les paramètres. Les outils spécialisés restent préférables lorsqu'ils couvrent exactement la demande."
        };
        internal static object Run(UIApplication app, Document doc, JObject args, Func<string, string, bool> confirm)
        {
            CodexFamilyDesign.Keys(args, "document_key", "description", "validate_only", "program_json");
            if (doc == null || !doc.IsFamilyDocument || doc.IsReadOnly || doc.IsModifiable ||
                CodexFamilyDesign.String(args, "document_key", 100) != doc.OwnerFamily.UniqueId)
                throw new InvalidOperationException("Ouvrir une famille modifiable et relire revit_inspect_family avant le programme.");
            if (args["validate_only"]?.Type != JTokenType.Boolean) throw new InvalidOperationException("validate_only doit être un booléen.");
            bool validate = (bool)args["validate_only"];
            string description = CodexFamilyDesign.String(args, "description", 1000);
            string source = CodexFamilyDesign.String(args, "program_json", 200000);
            JObject program;
            using (var reader = new JsonTextReader(new System.IO.StringReader(source)) { MaxDepth = 40, DateParseHandling = DateParseHandling.None })
                program = JObject.Load(reader);
            CodexFamilyDesign.Keys(program, "steps", "outputs");
            int count = 0; ValidateSteps(program["steps"], 0, ref count);
            var outputs = CodexFamilyDesign.Items(program, "outputs", 0, 100).Select(t => (string)t).ToArray();
            if (outputs.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("Nom de sortie invalide.");
            if (!validate && !confirm("Appliquer le programme à la famille", description + "\n\nUn Ctrl+Z annule le programme. Aucun enregistrement.\n\n" + source))
                throw new InvalidOperationException("Programme refusé. Ne pas réessayer sans nouvelle demande.");
            var engine = new CodexFamilyProgram(app, doc, new Budget());
            using (var guard = new CodexCreationGuard(app.Application, description))
            using (var group = new TransactionGroup(doc, "Codex — programme de famille"))
            {
                group.Start();
                try
                {
                    var before = new HashSet<string>(new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e => e.UniqueId));
                    engine.Steps((JArray)program["steps"]); engine.Commit();
                    var report = outputs.ToDictionary(name => name, name => engine.Describe(engine.Reference(name)));
                    var after = new HashSet<string>(new FilteredElementCollector(doc).WhereElementIsNotElementType().Select(e => e.UniqueId));
                    int added = after.Count(id => !before.Contains(id)), removed = before.Count(id => !after.Contains(id));
                    if (validate) group.RollBack();
                    else if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Programme annulé par Revit.");
                    guard.Complete();
                    return new { validated = true, applied = !validate, saved = false, outputs = report, added_elements = added, removed_elements = removed,
                        executed_steps = engine.budget.Steps, warnings = engine.warnings.ToArray(),
                        note = validate ? "Test annulé : état initial restauré. IDs temporaires, non réutilisables. Appliquer le même programme pour modifier la famille." : "Famille modifiée en place. Un Ctrl+Z annule le programme. construction.json reste historique." };
                }
                finally { engine.Abort(); }
            }
        }
        private static readonly Dictionary<string, string[]> KeysByOp = new Dictionary<string, string[]> {
            ["new"] = new[] { "type", "args", "signature" }, ["static"] = new[] { "type", "member", "args", "signature" },
            ["call"] = new[] { "target", "member", "args", "signature" }, ["get"] = new[] { "target", "type", "member" },
            ["set"] = new[] { "target", "member", "value" }, ["enum"] = new[] { "type", "value" }, ["typeof"] = new[] { "type" },
            ["list"] = new[] { "type", "items" }, ["item"] = new[] { "target", "index" },
            ["foreach"] = new[] { "target", "var", "steps" }, ["nested"] = new[] { "name", "hosting", "source_family", "overwrite_parameter_values", "steps" },
            ["mm"] = new[] { "value" }, ["xyz_mm"] = new[] { "args" }, ["math"] = new[] { "member", "args" }, ["assert"] = new[] { "value", "message" },
            ["range"] = new[] { "args" }, ["if"] = new[] { "value", "then", "else" }
        };
        private static void ValidateSteps(JToken token, int depth, ref int count)
        {
            if (!(token is JArray steps) || steps.Count == 0 || depth > 8) throw new InvalidOperationException("steps exige une liste non vide ; profondeur maximale 8.");
            foreach (var item in steps)
            {
                if (++count > 1500 || !(item is JObject step)) throw new InvalidOperationException("Programme invalide ou dépassant 1500 étapes.");
                string op = (string)step["op"];
                if (op == null || !KeysByOp.TryGetValue(op, out var allowed) || step.Properties().Any(p => p.Name != "op" && p.Name != "id" && !allowed.Contains(p.Name)))
                    throw new InvalidOperationException("Opération ou champ inconnu : " + op);
                if (step["id"] != null) Name(step["id"]);
                if (step["steps"] != null) ValidateSteps(step["steps"], depth + 1, ref count);
                if (step["then"] != null) ValidateSteps(step["then"], depth + 1, ref count);
                if (step["else"] != null) ValidateSteps(step["else"], depth + 1, ref count);
            }
        }
        private static string Name(JToken token)
        {
            string name = token?.Type == JTokenType.String ? (string)token : null;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || Reserved.Contains(name)) throw new InvalidOperationException("Identifiant vide, trop long ou réservé.");
            return name;
        }
        private object Reference(string name)
        { if (name == null || !variables.TryGetValue(name, out var value)) throw new InvalidOperationException("Référence inconnue : " + name); return value; }
        private object Value(JToken token)
        {
            if (token == null) throw new InvalidOperationException("Argument manquant.");
            if (token is JObject reference)
            { CodexFamilyDesign.Keys(reference, "ref"); return Reference((string)reference["ref"]); }
            if (!(token is JValue scalar)) throw new InvalidOperationException("Utiliser op=list pour une collection, ou {ref:'nom'}.");
            if (scalar.Type == JTokenType.Null) return null;
            if (scalar.Type == JTokenType.String || scalar.Type == JTokenType.Boolean) return scalar.Value;
            if (scalar.Type == JTokenType.Integer || scalar.Type == JTokenType.Float) { Number(scalar.Value); return scalar.Value; }
            throw new InvalidOperationException("Littéral non pris en charge.");
        }
        private object[] Args(JObject step, string key = "args")
        {
            if (!(step[key] is JArray args) || args.Count > 1000) throw new InvalidOperationException(key + " doit être une liste de 0 à 1000 valeurs.");
            return args.Select(Value).ToArray();
        }
        private void Start()
        {
            if (transaction != null) return;
            transaction = new Transaction(doc, "Codex — opérations natives"); transaction.Start();
            transaction.SetFailureHandlingOptions(transaction.GetFailureHandlingOptions().SetFailuresPreprocessor(new Failures(warnings)).SetClearAfterRollback(true));
        }
        private void Commit()
        {
            if (transaction == null) return;
            var t = transaction; transaction = null;
            using (t) if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit a annulé les opérations : " + string.Join(" ; ", warnings));
        }
        private void Abort() { if (transaction != null) { transaction.Dispose(); transaction = null; } }
        private void Steps(JArray steps)
        {
            foreach (JObject step in steps)
            {
                if (++budget.Steps > 10000) throw new InvalidOperationException("Budget de 10000 opérations dépassé.");
                CodexCreationGuard.Check("programme : " + ((string)step["id"] ?? (string)step["op"]));
                try
                {
                    object result = Step(step); CheckValue(result);
                    if (step["id"] != null) variables[Name(step["id"])] = result;
                }
                catch (Exception ex)
                {
                    if (ex is TargetInvocationException invocation && invocation.InnerException != null) ex = invocation.InnerException;
                    throw new InvalidOperationException("Étape « " + ((string)step["id"] ?? (string)step["op"]) + " » : " + ex.Message, ex);
                }
            }
        }
        private object Step(JObject s)
        {
            string op = (string)s["op"];
            if (op == "if")
            {
                if (!(Value(s["value"]) is bool condition)) throw new InvalidOperationException("Condition booléenne attendue.");
                var branch = s[condition ? "then" : "else"];
                if (branch != null) Steps(branch as JArray ?? throw new InvalidOperationException("Branche invalide."));
                else if (condition) throw new InvalidOperationException("Branche then manquante.");
                return condition;
            }
            if (op == "range")
            {
                var args = Args(s); if (args.Length != 3) throw new InvalidOperationException("range exige début, nombre, pas.");
                double start = Number(args[0]), count = Number(args[1]), step = Number(args[2]);
                if (count < 0 || count > 200 || count != Math.Truncate(count)) throw new InvalidOperationException("range limité à 200 valeurs.");
                return Enumerable.Range(0, (int)count).Select(i => Number(start + i * step)).ToList();
            }
            if (op == "nested") return Nested(s);
            if (op == "foreach")
            {
                var entries = Sequence(Value(s["target"]), 200); string variable = Name(s["var"]);
                var outer = new Dictionary<string, object>(variables);
                try { foreach (var entry in entries) { variables.Clear(); foreach (var pair in outer) variables.Add(pair.Key, pair.Value); variables[variable] = entry; Steps(s["steps"] as JArray ?? throw new InvalidOperationException("steps manquant.")); } }
                finally { variables.Clear(); foreach (var pair in outer) variables.Add(pair.Key, pair.Value); }
                return entries.Length;
            }
            if (op == "mm") return Number(Value(s["value"])) / 304.8;
            if (op == "xyz_mm") { var a = Args(s); if (a.Length != 3) throw new InvalidOperationException("xyz_mm exige trois valeurs."); return new XYZ(Number(a[0]) / 304.8, Number(a[1]) / 304.8, Number(a[2]) / 304.8); }
            if (op == "math") return MathValue((string)s["member"], Args(s));
            if (op == "assert") { if (!(Value(s["value"]) is bool condition) || !condition) throw new InvalidOperationException("Assertion échouée : " + (string)s["message"]); return true; }
            if (op == "typeof") return ResolveType((string)s["type"]);
            if (op == "enum") { var type = ResolveType((string)s["type"]); if (!type.IsEnum) throw new InvalidOperationException("Type enum attendu."); return ConvertValue(Value(s["value"]), type); }
            if (op == "list")
            {
                var type = ResolveType((string)s["type"]);
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
                foreach (var item in Args(s, "items")) list.Add(ConvertValue(item, type));
                return list;
            }
            if (op == "item")
            {
                var entries = Sequence(Value(s["target"]), 1000); double index = Number(Value(s["index"]));
                if (index != Math.Truncate(index) || index < 0 || index >= entries.Length) throw new InvalidOperationException("Index hors limites.");
                return entries[(int)index];
            }
            Start();
            object target = s["target"] == null ? null : Value(s["target"]);
            if (target != null) CheckValue(target);
            Type owner = target?.GetType() ?? ResolveType((string)s["type"]);
            string member = (string)s["member"];
            if (op == "new") return Invoke(owner.GetConstructors().Cast<MethodBase>(), null, Args(s), s);
            CheckMember(owner, member, target);
            var flags = BindingFlags.Public | (target == null ? BindingFlags.Static : BindingFlags.Instance);
            if (op == "get" || op == "set")
            {
                var property = owner.GetProperty(member, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    if (op == "get") return property.GetValue(target);
                    if (!property.CanWrite || property.SetMethod == null || !property.SetMethod.IsPublic) throw new InvalidOperationException("Propriété non modifiable.");
                    property.SetValue(target, ConvertValue(Value(s["value"]), property.PropertyType)); return null;
                }
                if (op == "get") { var field = owner.GetField(member, flags); if (field != null) return field.GetValue(target); }
                throw new InvalidOperationException("Propriété/champ introuvable : " + member);
            }
            if (op != "call" && op != "static") throw new InvalidOperationException("Opération inconnue : " + op);
            if (op == "call" && target == null) throw new InvalidOperationException("Cible d'appel absente.");
            var arguments = Args(s);
            if (owner == typeof(Line) && member == "CreateBound" && arguments.Length == 2)
                return CodexCreationGuard.CreateLine((XYZ)arguments[0], (XYZ)arguments[1]);
            return Invoke(owner.GetMethods(flags).Where(m => m.Name == member).Cast<MethodBase>(), target, arguments, s);
        }
        private object Nested(JObject s)
        {
            if (depth >= 3) throw new InvalidOperationException("Imbrication maximale : trois niveaux.");
            Family existing = null; string name = null, hosting = null;
            bool overwrite = false;
            if (s["source_family"] != null)
            {
                existing = Value(s["source_family"]) as Family ?? throw new InvalidOperationException("source_family exige une Family déjà chargée.");
                CheckValue(existing);
                if (!existing.IsEditable || existing.IsInPlace) throw new InvalidOperationException("Cette famille imbriquée n'est pas éditable.");
                if (s["name"] != null || s["hosting"] != null) throw new InvalidOperationException("L'édition imbriquée conserve le nom et l'hébergement.");
                if (s["overwrite_parameter_values"]?.Type != JTokenType.Boolean) throw new InvalidOperationException("Préciser overwrite_parameter_values en édition imbriquée.");
                overwrite = (bool)s["overwrite_parameter_values"];
            }
            else
            {
                name = CodexFamilyBuilder.SafeName(CodexFamilyDesign.String(s, "name", 70)); hosting = (string)s["hosting"];
                if (!new[] { "free", "wall", "floor", "ceiling", "face", "work_plane" }.Contains(hosting)) throw new InvalidOperationException("Hébergement inconnu.");
                if (new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Famille imbriquée homonyme : " + name + ". Réutiliser/éditer la famille existante ou choisir un nom distinct.");
            }
            string existingKey = existing?.UniqueId, existingName = existing?.Name;
            var instances = existing == null ? new Dictionary<string, string>() :
                new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(i => i.Symbol.Family.Id == existing.Id).ToDictionary(i => i.UniqueId, i => i.Symbol.Name);
            Commit(); Document child = null; CodexFamilyProgram engine = null;
            try
            {
                child = existing == null ? app.Application.NewFamilyDocument(CodexFamilyBuilder.FindTemplate(app, hosting)) : doc.EditFamily(existing);
                engine = new CodexFamilyProgram(app, child, budget, depth + 1); engine.Start();
                if (child.FamilyManager.CurrentType == null) child.FamilyManager.NewType("Standard");
                if (hosting == "work_plane") child.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_WORK_PLANE_BASED).Set(1);
                engine.Steps(s["steps"] as JArray ?? throw new InvalidOperationException("steps manquant.")); engine.Commit();
                warnings.AddRange(engine.warnings);
                var loaded = (existing == null ? child.LoadFamily(doc) : child.LoadFamily(doc, new ReloadOptions(existingName, overwrite))) ?? throw new InvalidOperationException("Chargement de la famille imbriquée refusé.");
                if (existing != null)
                {
                    // Revit can replace the Family definition on reload. Placed instances must survive.
                    if (loaded.Name != existingName || new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().Count(f => f.Name == existingName) != 1 ||
                        instances.Any(pair => !(doc.GetElement(pair.Key) is FamilyInstance instance) || instance.Symbol.Family.Id != loaded.Id || instance.Symbol.Name != pair.Value))
                        throw new InvalidOperationException("Le rechargement altère les instances ou crée un doublon ; programme annulé.");
                    if (loaded.UniqueId != existingKey) warnings.Add("Revit a remplacé l'identifiant de la définition imbriquée au rechargement ; instances et types placés conservés. Relire les identifiants après application.");
                }
                Start(); if (existing == null) loaded.Name = name; return loaded;
            }
            finally { engine?.Abort(); if (child != null && child.IsValidObject) child.Close(false); }
        }
        private object Invoke(IEnumerable<MethodBase> methods, object target, object[] values, JObject step)
        {
            var candidates = new List<Tuple<MethodBase, object[]>>();
            string[] signature = step["signature"] is JArray specified ? specified.Select(t => (string)t).ToArray() : null;
            foreach (var method in methods.Where(m => !m.ContainsGenericParameters && m.GetParameters().Length == values.Length))
            {
                var parameters = method.GetParameters();
                if (parameters.Any(p => p.ParameterType.IsByRef || p.IsOut) || signature != null && !parameters.Select(p => TypeName(p.ParameterType)).SequenceEqual(signature)) continue;
                try { candidates.Add(Tuple.Create(method, parameters.Select((p, i) => ConvertValue(values[i], p.ParameterType)).ToArray())); }
                catch (InvalidOperationException) { }
            }
            if (candidates.Count != 1) throw new InvalidOperationException(candidates.Count == 0 ? "Aucune signature compatible. Lire revit_family_api." : "Surcharge ambiguë : fournir signature depuis revit_family_api.");
            var chosen = candidates[0];
            if (chosen.Item1.DeclaringType == typeof(LinearArray) || chosen.Item1.DeclaringType == typeof(RadialArray))
                foreach (var pair in chosen.Item1.GetParameters().Select((p,i) => new { p, value = chosen.Item2[i] }))
                    if (pair.p.ParameterType == typeof(int) && pair.p.Name.IndexOf("number", StringComparison.OrdinalIgnoreCase) >= 0 && (int)pair.value > 200)
                        throw new InvalidOperationException("Réseau limité à 200 membres par opération.");
            object result = chosen.Item1 is ConstructorInfo constructor ? constructor.Invoke(chosen.Item2) : ((MethodInfo)chosen.Item1).Invoke(target, chosen.Item2);
            return result;
        }
        private object ConvertValue(object value, Type type)
        {
            if (value == null) { if (!type.IsValueType || Nullable.GetUnderlyingType(type) != null) return null; throw new InvalidOperationException("Valeur null incompatible."); }
            CheckValue(value);
            if (type.IsInstanceOfType(value)) return value;
            if (type.IsEnum && value is string text)
            {
                try { var parsed = Enum.Parse(type, text, false); if (Enum.IsDefined(type, parsed)) return parsed; } catch (ArgumentException) { }
                throw new InvalidOperationException("Enum invalide : " + text);
            }
            if (type == typeof(double) || type == typeof(float)) return Convert.ChangeType(Number(value), type, CultureInfo.InvariantCulture);
            if (type == typeof(int) || type == typeof(long) || type == typeof(byte) || type == typeof(short))
            {
                double number = Number(value);
                if (number != Math.Truncate(number)) throw new InvalidOperationException("Valeur entière attendue.");
                try { return Convert.ChangeType(number, type, CultureInfo.InvariantCulture); } catch (OverflowException) { throw new InvalidOperationException("Entier hors limites."); }
            }
            if (type == typeof(Guid) && value is string guid && Guid.TryParse(guid, out var result)) return result;
            throw new InvalidOperationException("Type incompatible avec " + TypeName(type));
        }
        private static double Number(object value)
        {
            if (!(value is double || value is float || value is int || value is long || value is short || value is byte || value is decimal)) throw new InvalidOperationException("Nombre attendu.");
            double n = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(n) || double.IsInfinity(n) || Math.Abs(n) > 1e12) throw new InvalidOperationException("Nombre non fini ou hors limites.");
            return n;
        }
        private static object MathValue(string op, object[] values)
        {
            if (op == "equal" && values.Length == 2 && (values[0] is string || values[0] is bool)) return Equals(values[0], values[1]);
            var a = values.Select(Number).ToArray();
            bool unary = new[] { "sin", "cos", "floor", "abs" }.Contains(op);
            if (a.Length != (unary ? 1 : 2)) throw new InvalidOperationException("Nombre d'arguments math incorrect.");
            switch (op)
            {
                case "add": return a[0] + a[1]; case "subtract": return a[0] - a[1]; case "multiply": return a[0] * a[1]; case "divide": return a[0] / a[1];
                case "min": return Math.Min(a[0], a[1]); case "max": return Math.Max(a[0], a[1]);
                case "sin": return Math.Sin(a[0]); case "cos": return Math.Cos(a[0]); case "floor": return Math.Floor(a[0]); case "abs": return Math.Abs(a[0]);
                case "equal": return a[0] == a[1]; case "less": return a[0] < a[1]; case "greater": return a[0] > a[1];
                default: throw new InvalidOperationException("Opération math inconnue.");
            }
        }
        private object[] Sequence(object value, int max)
        {
            CheckValue(value);
            if (!(value is IEnumerable sequence) || value is string) throw new InvalidOperationException("Collection attendue.");
            var values = sequence.Cast<object>().Take(max + 1).ToArray();
            if (values.Length > max) throw new InvalidOperationException("Collection dépassant " + max + " éléments ; affiner le filtre.");
            foreach (var item in values) CheckValue(item);
            return values;
        }
        private void CheckValue(object value)
        {
            if (value == null || value is string || value is bool || value is Guid) return;
            if (value is double || value is float || value is int || value is long || value is byte || value is short || value is decimal) { Number(value); return; }
            if (value is Type apiType) { if (!AllowedType(apiType) && !ScalarTypes.Contains(apiType)) throw new InvalidOperationException("Type non accessible."); return; }
            if (value is Document document && !document.Equals(doc) || value is Element element && !element.Document.Equals(doc))
                throw new InvalidOperationException("Objet provenant d'un autre document.");
            if (value is Curve curve && curve.IsBound && curve.Length + 1e-12 < Math.Max(1 / 304.8, app.Application.ShortCurveTolerance * 1.01))
                throw new InvalidOperationException("Courbe inférieure à 1 mm ou à la tolérance Revit.");
            if (!AllowedType(value.GetType()) && !(value is IEnumerable) && !(value is IList)) throw new InvalidOperationException("Objet non accessible : " + value.GetType().FullName);
        }
        private object Describe(object value)
        {
            CheckValue(value);
            if (value == null || value is string || value is bool || value.GetType().IsPrimitive || value is decimal) return value;
            if (value is Element e) return new { type = e.GetType().FullName, id = e.Id.ToString(), unique_id = e.UniqueId, name = e.Name };
            if (value is ElementId id) return id.ToString();
            if (value is XYZ point) return new { xyz_internal = new[] { point.X, point.Y, point.Z }, xyz_mm = new[] { point.X * 304.8, point.Y * 304.8, point.Z * 304.8 } };
            if (value is Type type) return new { api_type = TypeName(type) };
            if (value is Enum) return value.ToString();
            if (value is IEnumerable) return Sequence(value, 1000).Select(Describe).ToArray();
            return new { type = value.GetType().FullName };
        }
        private static readonly string[] ForbiddenTypeParts = { "Transaction", "Application", "Print", "Export", "Import", "Image", "Link", "PointCloud", "External", "Transmission", "Worksharing", "ModelPath", "FileInfo", "FileAccess", "DefinitionFile", "FileDialog", "Cloud", "PerformanceAdviser", "Updater", "ExtensibleStorage" };
        private static readonly Type[] ScalarTypes = { typeof(double), typeof(int), typeof(long), typeof(string), typeof(bool) };
        private static bool AllowedType(Type type)
        {
            if (type.IsEnum && type.Assembly == typeof(Document).Assembly) return true;
            if (type.Assembly != typeof(Document).Assembly || !(type.Namespace == "Autodesk.Revit.DB" || type.Namespace?.StartsWith("Autodesk.Revit.DB.", StringComparison.Ordinal) == true || type.Namespace == "Autodesk.Revit.Creation")) return false;
            return !ForbiddenTypeParts.Any(part => type.FullName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        private static Type ResolveType(string name)
        {
            var scalar = ScalarTypes.FirstOrDefault(t => t.FullName == name); if (scalar != null) return scalar;
            var type = name == null ? null : typeof(Document).Assembly.GetType(name, false, false);
            if (type == null || !AllowedType(type)) throw new InvalidOperationException("Type API absent ou non exposé : " + name + ". Consulter revit_family_api.");
            return type;
        }
        private static readonly string[] DocumentMembers = { "GetElement", "Delete", "Regenerate", "FamilyCreate", "FamilyManager", "OwnerFamily", "ActiveView", "Settings", "Title", "IsFamilyDocument", "GetUnits" };
        private static void CheckMember(Type type, string name, object target)
        {
            if (string.IsNullOrEmpty(name) || name == "GetType" || name == "Dispose" || name == "Finalize" || name == "GetHashCode" || name.StartsWith("add_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal))
                throw new InvalidOperationException("Membre non exposé.");
            if (typeof(Type).IsAssignableFrom(type)) throw new InvalidOperationException("La réflexion .NET n'est pas exposée.");
            if (target is Document && !DocumentMembers.Contains(name)) throw new InvalidOperationException("Méthode Document non exposée : " + name + ". nested gère les familles temporaires ; aucune ouverture/sauvegarde/export n'est autorisée.");
            if (!AllowedType(type) && !(target is IList && new[] { "Add", "Clear", "Contains", "Remove", "get_Item", "get_Count", "Count" }.Contains(name)))
                throw new InvalidOperationException("Membre hors API du modèle.");
            if (new[] { "Save", "Load", "Open", "Close", "Export", "Import", "Write", "SubmitPrint" }.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) ||
                name == "EditFamily" || name.IndexOf("FromFile", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("ToFile", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("Opération externe non exposée : " + name);
        }
        private static string TypeName(Type type) => type.FullName ?? type.Name;
        internal static object Api(JObject args)
        {
            CodexFamilyDesign.Keys(args, "type_name", "member_name", "offset", "limit");
            string name = (string)args["type_name"], member = (string)args["member_name"];
            int offset = checked((int)CodexFamilyDesign.Scalar(args["offset"], "offset", 0, 100000));
            int limit = checked((int)CodexFamilyDesign.Scalar(args["limit"], "limit", 1, 100));
            if (name == null || member == null || name.Length > 200 || member.Length > 100) throw new InvalidOperationException("Noms API invalides.");
            if (name.Length == 0)
            {
                var types = typeof(Document).Assembly.GetExportedTypes().Where(AllowedType).Select(TypeName).Where(n => n.IndexOf(member, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(n => n).ToArray();
                return new { types = types.Skip(offset).Take(limit).ToArray(), total = types.Length, next_offset = offset + limit < types.Length ? (int?)(offset + limit) : null };
            }
            var type = ResolveType(name);
            if (type.IsEnum) return new { type = name, enum_values = Enum.GetNames(type) };
            var entries = new List<object>();
            foreach (var constructor in type.GetConstructors()) entries.Add(Signature(constructor));
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Where(m => m.DeclaringType != typeof(object) && !m.Name.StartsWith("add_") && !m.Name.StartsWith("remove_")))
                entries.Add(Signature(method));
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                entries.Add(new { name = property.Name, kind = "property", value_type = TypeName(property.PropertyType), writable = property.SetMethod?.IsPublic == true, is_static = property.GetMethod?.IsStatic == true });
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)) entries.Add(new { name = field.Name, kind = "field", value_type = TypeName(field.FieldType) });
            var result = entries.Select(JObject.FromObject).Where(e => ((string)e["name"]).IndexOf(member, StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(e => (string)e["name"]).ToArray();
            return new { type = name, members = result.Skip(offset).Take(limit).ToArray(), total = result.Length,
                next_offset = offset + limit < result.Length ? (int?)(offset + limit) : null,
                note = "Signatures de la version Revit en cours. Les limites d'accès du contrat s'appliquent même si un membre figure dans cette liste. signature utilise exactement les chaînes parameter_types." };
        }
        private static object Signature(MethodBase method) => new { name = method is ConstructorInfo ? ".ctor" : method.Name,
            kind = method is ConstructorInfo ? "constructor" : "method", is_static = method.IsStatic,
            parameter_names = method.GetParameters().Select(p => p.Name).ToArray(), parameter_types = method.GetParameters().Select(p => TypeName(p.ParameterType)).ToArray(),
            return_type = method is MethodInfo info ? TypeName(info.ReturnType) : TypeName(method.DeclaringType),
            callable_signature = !method.ContainsGenericParameters && !method.GetParameters().Any(p => p.ParameterType.IsByRef || p.IsOut) };
        private sealed class Failures : IFailuresPreprocessor
        {
            private readonly List<string> warnings; internal Failures(List<string> warnings) { this.warnings = warnings; }
            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                bool error = false;
                foreach (var failure in accessor.GetFailureMessages())
                { warnings.Add(failure.GetDescriptionText()); if (failure.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(failure); else error = true; }
                return error ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
            }
        }
        private sealed class ReloadOptions : IFamilyLoadOptions
        {
            private readonly bool overwrite; private readonly string targetName;
            internal ReloadOptions(string targetName, bool overwrite) { this.targetName = targetName; this.overwrite = overwrite; }
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = overwrite; return true; }
            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            { bool target = sharedFamily.Name == targetName; source = target ? FamilySource.Family : FamilySource.Project; overwriteParameterValues = target && overwrite; return true; }
        }
    }
}
