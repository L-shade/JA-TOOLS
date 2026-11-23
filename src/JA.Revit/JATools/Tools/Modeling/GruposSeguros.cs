using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;

namespace RevitDwgUnifier.Tools.Modeling
{
    /// <summary>
    /// Failures preprocessor for changes that may touch grouped elements: warnings are dismissed,
    /// "joined but do not intersect" is resolved by unjoining, and ANY error (e.g. "changes to groups
    /// are allowed only in group edit mode", which Revit would solve by ungrouping) rolls the
    /// transaction back — a group is never ungrouped behind the user's back.
    /// </summary>
    public sealed class GuardaGrupos : IFailuresPreprocessor
    {
        public bool Desfez;
        public int Disjuntos;
        private static readonly FailureDefinitionId Disjunto = LerDisjunto();

        private static FailureDefinitionId LerDisjunto()
        {
            try
            {
                Type t = typeof(BuiltInFailures).GetNestedType("JoinElementsFailures", BindingFlags.Public);
                foreach (string nome in new[] { "JoiningDisjointWarn", "JoiningDisjoint" })
                    if (t?.GetProperty(nome, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is FailureDefinitionId id) return id;
            }
            catch { }
            return null;
        }

        public void Anexar(Transaction t)
        {
            FailureHandlingOptions o = t.GetFailureHandlingOptions();
            o.SetFailuresPreprocessor(this);
            o.SetClearAfterRollback(true);
            t.SetFailureHandlingOptions(o);
        }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor fa)
        {
            bool erro = false, resolveu = false;
            foreach (FailureMessageAccessor m in fa.GetFailureMessages())
            {
                if (m.GetSeverity() != FailureSeverity.Warning) { erro = true; continue; }
                if (Disjunto != null && m.GetFailureDefinitionId().Guid == Disjunto.Guid && Enum.TryParse("UnjoinElements", out FailureResolutionType unjoin) && m.HasResolutionOfType(unjoin))
                {
                    try
                    {
                        m.SetCurrentResolutionType(unjoin);
                        fa.ResolveFailure(m);
                        Disjuntos++;
                        resolveu = true;
                        continue;
                    }
                    catch { }
                }
                fa.DeleteWarning(m);
            }
            if (erro) { Desfez = true; return FailureProcessingResult.ProceedWithRollBack; }
            return resolveu ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
        }
    }

    /// <summary>
    /// Changes inside a group type with several copies, without leaving anything ungrouped: one copy
    /// is ungrouped, changed, grouped again; every other copy is switched to the new type and moved
    /// back so its members stay where they were; the old type is deleted and the new one takes its
    /// name. One undo; if any step fails, everything is rolled back.
    /// </summary>
    public static class GruposSeguros
    {
        /// <summary>Why the type cannot be rebuilt safely (null = it can).</summary>
        public static string Impedimento(Document doc, GroupType tipo, Group g0)
        {
            if (g0 == null || tipo == null) return "grupo não encontrado";
            if (g0.GroupId != ElementId.InvalidElementId) return "grupo dentro de outro grupo";
            int n0 = g0.GetMemberIds().Count;
            if (Copias(doc, tipo).Any(g => g.GetMemberIds().Count != n0)) return "há cópias com membros excluídos";
            try { if (tipo.GetAvailableAttachedDetailGroupTypeIds().Count > 0) return "tem grupo de detalhe anexado (seria perdido)"; } catch { }
            return null;
        }

        public static List<Group> Copias(Document doc, GroupType tipo) =>
            new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>().Where(g => g.GroupType?.Id == tipo.Id).ToList();

        /// <summary>
        /// Runs <paramref name="alterar"/> on the members of <paramref name="g0"/> (ungrouped) and
        /// rebuilds the type. Returns null when done, otherwise why not (nothing changed).
        /// </summary>
        public static string RefazerTipo(Document doc, GroupType tipo, Group g0, Action<ICollection<ElementId>> alterar, out int disjuntos)
        {
            disjuntos = 0;
            string imp = Impedimento(doc, tipo, g0);
            if (imp != null) return imp;
            string nome = tipo.Name;
            var outras = Copias(doc, tipo).Where(g => g.Id != g0.Id).ToList();
            var antes = outras.ToDictionary(g => g.Id, g => Centro(doc, g));
            string comentario = g0.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
            bool fixado = g0.Pinned;
            XYZ origemAntiga = (g0.Location as LocationPoint)?.Point ?? XYZ.Zero;

            using (var tg = new TransactionGroup(doc, "JA Tools - grupo " + nome))
            {
                tg.Start();
                ICollection<ElementId> membros;
                var guarda = new GuardaGrupos();
                using (var t = new Transaction(doc, "Desagrupar e alterar"))
                {
                    t.Start();
                    guarda.Anexar(t);
                    try
                    {
                        membros = g0.UngroupMembers();
                        alterar(membros);
                    }
                    catch (Exception ex) { t.RollBack(); tg.RollBack(); return "não deu para alterar: " + ex.Message; }
                    if (t.Commit() != TransactionStatus.Committed || guarda.Desfez) { tg.RollBack(); return "o Revit recusou a alteração"; }
                }
                disjuntos += guarda.Disjuntos;
                Group novo;
                guarda = new GuardaGrupos();
                using (var t = new Transaction(doc, "Agrupar de novo"))
                {
                    t.Start();
                    guarda.Anexar(t);
                    try
                    {
                        novo = doc.Create.NewGroup(membros);
                        try { if (comentario != null) novo.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(comentario); } catch { }
                        foreach (Group o in outras) o.GroupType = novo.GroupType;
                    }
                    catch (Exception ex) { t.RollBack(); tg.RollBack(); return "não deu para agrupar de novo: " + ex.Message; }
                    if (t.Commit() != TransactionStatus.Committed || guarda.Desfez) { tg.RollBack(); return "o Revit recusou o tipo novo"; }
                }
                guarda = new GuardaGrupos();
                using (var t = new Transaction(doc, "Ajustar cópias"))
                {
                    t.Start();
                    guarda.Anexar(t);
                    try
                    {
                        doc.Regenerate();
                        // the new type's origin is elsewhere: put each copy's members back where they were.
                        // In plan: by the centre of the members (rotation/mirror proof, heights don't change it);
                        // in height: by how much the origin moved in the copy that was rebuilt.
                        XYZ origemNova = (novo.Location as LocationPoint)?.Point ?? origemAntiga;
                        double dz = origemNova.Z - origemAntiga.Z;
                        foreach (Group o in outras)
                        {
                            XYZ c = Centro(doc, o);
                            var d = new XYZ(antes[o.Id].X - c.X, antes[o.Id].Y - c.Y, dz);
                            if (d.GetLength() > 1e-6) ElementTransformUtils.MoveElement(doc, o.Id, d);
                        }
                        doc.Delete(tipo.Id);
                        novo.GroupType.Name = nome;
                        if (fixado) novo.Pinned = true;
                    }
                    catch (Exception ex) { t.RollBack(); tg.RollBack(); return "não deu para ajustar as cópias: " + ex.Message; }
                    if (t.Commit() != TransactionStatus.Committed || guarda.Desfez) { tg.RollBack(); return "o Revit recusou o ajuste das cópias"; }
                }
                tg.Assimilate();
            }
            return null;
        }

        /// <summary>Centre of the members' boxes (survives type swaps, rotation and mirroring); only X/Y are used.</summary>
        public static XYZ Centro(Document doc, Group g)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            foreach (ElementId id in g.GetMemberIds())
            {
                BoundingBoxXYZ b = doc.GetElement(id)?.get_BoundingBox(null);
                if (b == null) continue;
                x0 = Math.Min(x0, b.Min.X); y0 = Math.Min(y0, b.Min.Y); z0 = Math.Min(z0, b.Min.Z);
                x1 = Math.Max(x1, b.Max.X); y1 = Math.Max(y1, b.Max.Y); z1 = Math.Max(z1, b.Max.Z);
            }
            if (x0 > x1) return (g.Location as LocationPoint)?.Point ?? XYZ.Zero;
            return new XYZ((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2);
        }
    }
}
