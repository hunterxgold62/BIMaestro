using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace BIMaestro.Codex
{
    // Restricted, unit-aware family expressions. Never invokes a scripting engine.
    internal sealed class FamilyValue
    {
        internal double Number; internal string Text; internal bool Boolean;
        internal string Kind; internal int LengthPower, AnglePower;
        internal static FamilyValue Numeric(double n, int length = 0, int angle = 0)
        {
            if (double.IsNaN(n) || double.IsInfinity(n) || Math.Abs(n) > 1e15) throw new InvalidOperationException("Résultat de formule non fini ou excessif.");
            return new FamilyValue { Kind = "numeric", Number = n, LengthPower = length, AnglePower = angle };
        }
        internal static FamilyValue Bool(bool value) => new FamilyValue { Kind = "yesno", Boolean = value };
        internal static FamilyValue String(string value) => new FamilyValue { Kind = "text", Text = value };
        internal bool SameUnit(FamilyValue other) => Kind == other.Kind && LengthPower == other.LengthPower && AnglePower == other.AnglePower;
        internal void RequireNumeric() { if (Kind != "numeric") throw new InvalidOperationException("Une valeur numérique est attendue."); }
        internal void RequireBoolean() { if (Kind != "yesno") throw new InvalidOperationException("Une condition Oui/Non est attendue."); }
    }

    internal sealed class FamilyFormula
    {
        internal string Op, Name; internal FamilyValue Literal; internal FamilyFormula[] Children = new FamilyFormula[0];
        internal IEnumerable<string> Dependencies => Name != null ? new[] { Name } : Children.SelectMany(c => c.Dependencies).Distinct();
        internal IEnumerable<FamilyFormula> Nodes => new[] { this }.Concat(Children.SelectMany(c => c.Nodes));
        internal FamilyValue Unit(Func<string, FamilyValue> get)
        {
            if (Literal != null) return Literal;
            if (Name != null) return get(Name);
            var values = Children.Select(c => c.Unit(get)).ToArray(); var a = values[0];
            if (Op == "if") { a.RequireBoolean(); if (!values[1].SameUnit(values[2])) throw new InvalidOperationException("Les deux branches de if doivent avoir la même unité et le même type."); return values[1]; }
            if (Op == "not" || Op == "and" || Op == "or") { foreach (var v in values) v.RequireBoolean(); return FamilyValue.Bool(true); }
            if (Op == "=" && values.All(v => v.Kind == "yesno")) return FamilyValue.Bool(true);
            foreach (var v in values) v.RequireNumeric();
            if (Op == "*" || Op == "/") return FamilyValue.Numeric(1, a.LengthPower + (Op == "*" ? 1 : -1) * values[1].LengthPower, a.AnglePower + (Op == "*" ? 1 : -1) * values[1].AnglePower);
            if (values.Length == 2)
            {
                if (!a.SameUnit(values[1])) throw new InvalidOperationException("Unités incompatibles dans la formule.");
                return new[] { "<", ">", "=", "<=", ">=" }.Contains(Op) ? FamilyValue.Bool(true) : a;
            }
            if (Op == "neg" || Op == "abs") return a;
            if (Op == "sqrt") { if (a.LengthPower % 2 != 0 || a.AnglePower % 2 != 0) throw new InvalidOperationException("Unités de racine carrée invalides."); return FamilyValue.Numeric(1, a.LengthPower / 2, a.AnglePower / 2); }
            if (Op == "sin" || Op == "cos" || Op == "tan") { if (a.LengthPower != 0 || a.AnglePower != 1) throw new InvalidOperationException("Angle attendu pour la trigonométrie."); return FamilyValue.Numeric(1); }
            if (a.LengthPower != 0 || a.AnglePower != 0) throw new InvalidOperationException("Nombre sans unité attendu.");
            return FamilyValue.Numeric(1, 0, Op == "asin" || Op == "acos" || Op == "atan" ? 1 : 0);
        }
        internal FamilyValue Evaluate(Func<string, FamilyValue> get)
        {
            if (Literal != null) return Literal;
            if (Name != null) return get(Name);
            var a = Children[0].Evaluate(get);
            if (Op == "if") { a.RequireBoolean(); return Children[a.Boolean ? 1 : 2].Evaluate(get); }
            if (Op == "not") { a.RequireBoolean(); return FamilyValue.Bool(!a.Boolean); }
            if (Op == "and" || Op == "or")
            {
                var items = Children.Select(c => c.Evaluate(get)).ToArray(); foreach (var item in items) item.RequireBoolean();
                return FamilyValue.Bool(Op == "and" ? items.All(v => v.Boolean) : items.Any(v => v.Boolean));
            }
            if (Children.Length == 1)
            {
                a.RequireNumeric();
                if (Op == "neg") return FamilyValue.Numeric(-a.Number, a.LengthPower, a.AnglePower);
                if (Op == "abs") return FamilyValue.Numeric(Math.Abs(a.Number), a.LengthPower, a.AnglePower);
                if (Op == "sqrt")
                { if (a.Number < 0 || a.LengthPower % 2 != 0 || a.AnglePower % 2 != 0) throw new InvalidOperationException("Racine carrée ou unités invalides."); return FamilyValue.Numeric(Math.Sqrt(a.Number), a.LengthPower / 2, a.AnglePower / 2); }
                if (Op == "sin" || Op == "cos" || Op == "tan")
                {
                    if (a.LengthPower != 0 || a.AnglePower != 1) throw new InvalidOperationException("La trigonométrie exige un angle (deg).");
                    double r = a.Number * Math.PI / 180;
                    if (Op == "tan" && Math.Abs(Math.Cos(r)) < 1e-10) throw new InvalidOperationException("Tangente indéfinie.");
                    return FamilyValue.Numeric(Op == "sin" ? Math.Sin(r) : Op == "cos" ? Math.Cos(r) : Math.Tan(r));
                }
                if (a.LengthPower != 0 || a.AnglePower != 0) throw new InvalidOperationException("Cette fonction attend un nombre sans unité.");
                switch (Op)
                {
                    case "round": return FamilyValue.Numeric(Math.Round(a.Number, MidpointRounding.AwayFromZero));
                    case "rounddown": return FamilyValue.Numeric(Math.Floor(a.Number));
                    case "roundup": return FamilyValue.Numeric(Math.Ceiling(a.Number));
                    case "asin": return FamilyValue.Numeric(Math.Asin(a.Number) * 180 / Math.PI, 0, 1);
                    case "acos": return FamilyValue.Numeric(Math.Acos(a.Number) * 180 / Math.PI, 0, 1);
                    case "atan": return FamilyValue.Numeric(Math.Atan(a.Number) * 180 / Math.PI, 0, 1);
                    default: throw new InvalidOperationException("Fonction inconnue : " + Op);
                }
            }
            var b = Children[1].Evaluate(get);
            if (Op == "=" && a.Kind == "yesno" && b.Kind == "yesno") return FamilyValue.Bool(a.Boolean == b.Boolean);
            a.RequireNumeric(); b.RequireNumeric();
            if (Op == "*" || Op == "/")
            {
                if (Op == "/" && Math.Abs(b.Number) < 1e-12) throw new InvalidOperationException("Division par zéro.");
                int sign = Op == "*" ? 1 : -1;
                return FamilyValue.Numeric(Op == "*" ? a.Number * b.Number : a.Number / b.Number, a.LengthPower + sign * b.LengthPower, a.AnglePower + sign * b.AnglePower);
            }
            if (!a.SameUnit(b)) throw new InvalidOperationException("Unités incompatibles dans la formule.");
            switch (Op)
            {
                case "+": return FamilyValue.Numeric(a.Number + b.Number, a.LengthPower, a.AnglePower);
                case "-": return FamilyValue.Numeric(a.Number - b.Number, a.LengthPower, a.AnglePower);
                case "<": return FamilyValue.Bool(a.Number < b.Number);
                case ">": return FamilyValue.Bool(a.Number > b.Number);
                case "=": return FamilyValue.Bool(a.Number == b.Number);
                case "<=": return FamilyValue.Bool(a.Number <= b.Number);
                case ">=": return FamilyValue.Bool(a.Number >= b.Number);
                default: throw new InvalidOperationException("Opérateur inconnu : " + Op);
            }
        }
        internal string Revit()
        {
            if (Name != null) return Name;
            if (Literal != null)
            {
                if (Literal.Kind == "text") return "\"" + Literal.Text + "\"";
                if (Literal.Kind == "yesno") return Literal.Boolean ? "1 = 1" : "1 = 0";
                return Literal.Number.ToString("0.################", CultureInfo.InvariantCulture) + (Literal.LengthPower == 1 ? " mm" : Literal.AnglePower == 1 ? "°" : "");
            }
            if (Op == "<=") return "not(" + Children[0].Revit() + " > " + Children[1].Revit() + ")";
            if (Op == ">=") return "not(" + Children[0].Revit() + " < " + Children[1].Revit() + ")";
            if (Op == "neg") return "(-(" + Children[0].Revit() + "))";
            if (new[] { "+", "-", "*", "/", "<", ">", "=" }.Contains(Op)) return "(" + Children[0].Revit() + " " + Op + " " + Children[1].Revit() + ")";
            return Op + "(" + string.Join(", ", Children.Select(c => c.Revit())) + ")";
        }
        internal static FamilyFormula Parse(string source) => new Parser(source).Parse();
        private sealed class Parser
        {
            private readonly List<string> tokens = new List<string>(); private int position, depth;
            internal Parser(string source)
            {
                if (string.IsNullOrWhiteSpace(source) || source.Length > 2000) throw new InvalidOperationException("Formule vide ou trop longue.");
                var regex = new Regex("\\G\\s*(<=|>=|[()+*/<>=,\\-]|[0-9]+(?:\\.[0-9]+)?|[A-Za-z][A-Za-z0-9_]*|\"[^\"\\r\\n]*\")");
                int at = 0;
                while (at < source.Length && !string.IsNullOrWhiteSpace(source.Substring(at)))
                { var match = regex.Match(source, at); if (!match.Success) throw new InvalidOperationException("Syntaxe de formule invalide près de : " + source.Substring(at)); tokens.Add(match.Groups[1].Value); at += match.Length; }
                if (tokens.Count > 300) throw new InvalidOperationException("Formule trop complexe.");
            }
            internal FamilyFormula Parse() { var node = Expr(0); if (position != tokens.Count) throw new InvalidOperationException("Texte inattendu dans la formule."); return node; }
            private string Peek => position < tokens.Count ? tokens[position] : "";
            private bool Take(string value) { if (Peek != value) return false; position++; return true; }
            private void Expect(string value) { if (!Take(value)) throw new InvalidOperationException("Formule : « " + value + " » attendu."); }
            private static int Precedence(string op) => new[] { "<", ">", "=", "<=", ">=" }.Contains(op) ? 1 : op == "+" || op == "-" ? 2 : op == "*" || op == "/" ? 3 : -1;
            private FamilyFormula Expr(int min)
            {
                if (++depth > 32) throw new InvalidOperationException("Formule trop imbriquée.");
                var left = Atom();
                while (Precedence(Peek) >= min) { string op = tokens[position++]; left = new FamilyFormula { Op = op, Children = new[] { left, Expr(Precedence(op) + 1) } }; }
                depth--; return left;
            }
            private FamilyFormula Atom()
            {
                if (Take("-")) return new FamilyFormula { Op = "neg", Children = new[] { Expr(4) } };
                if (Take("(")) { var node = Expr(0); Expect(")"); return node; }
                if (position >= tokens.Count) throw new InvalidOperationException("Formule incomplète.");
                string token = tokens[position++];
                if (double.TryParse(token, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double number))
                { int length = Take("mm") ? 1 : 0; int angle = length == 0 && Take("deg") ? 1 : 0; return new FamilyFormula { Literal = FamilyValue.Numeric(number, length, angle) }; }
                if (token.StartsWith("\"", StringComparison.Ordinal)) return new FamilyFormula { Literal = FamilyValue.String(token.Substring(1, token.Length - 2)) };
                if (token == "true" || token == "false") return new FamilyFormula { Literal = FamilyValue.Bool(token == "true") };
                if (!Regex.IsMatch(token, "^[A-Za-z][A-Za-z0-9_]*$")) throw new InvalidOperationException("Nom de formule invalide.");
                if (!Take("(")) return new FamilyFormula { Name = token };
                string function = token.ToLowerInvariant();
                var args = new List<FamilyFormula> { Expr(0) }; while (Take(",")) args.Add(Expr(0)); Expect(")");
                int count = function == "if" ? 3 : function == "and" || function == "or" ? args.Count : 1;
                if (!new[] { "if", "and", "or", "not", "abs", "sqrt", "round", "roundup", "rounddown", "sin", "cos", "tan", "asin", "acos", "atan" }.Contains(function) || args.Count != count || (function == "and" || function == "or") && count < 2)
                    throw new InvalidOperationException("Fonction ou nombre d'arguments invalide : " + token);
                return new FamilyFormula { Op = function, Children = args.ToArray() };
            }
        }
    }
}
