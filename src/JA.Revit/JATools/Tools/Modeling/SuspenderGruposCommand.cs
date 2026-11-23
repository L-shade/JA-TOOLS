using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Modeling
{
    /// <summary>
    /// Suspender Grupos (like ArchiCAD's "Suspend Groups"): the groups are taken apart so every
    /// element can be picked, moved, edited or deleted on its own; one more click puts them back.
    /// Which groups, members, origin and a "signature" of the geometry are kept IN THE MODEL
    /// (extensible storage on a DataStorage element), so the state survives saving, closing and
    /// other users. Restoring:
    ///   * a group nobody touched comes back as the same group type (copies stay copies);
    ///   * an edited group becomes a new type "… (editado)" — or keeps the name when it was the
    ///     only copy of its type;
    ///   * the elements keep their ids whenever possible (tags and dimensions survive).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class SuspenderGruposCommand : ToolCommand
    {
        public override string Title => "Suspender Grupos";

        protected override Result Run()
        {
            Estado est = GruposSuspensos.Ler(Doc);
            if (est.Grupos.Count > 0) return Restaurar(est);
            return Suspender(est);
        }

        // ================================================================== suspend

        private Result Suspender(Estado est)
        {
            List<Group> selecionados = UiDoc.Selection.GetElementIds().Select(id => Doc.GetElement(id))
                .Select(e => e is Group g ? g : e != null && e.GroupId != ElementId.InvalidElementId ? Topo(Doc.GetElement(e.GroupId) as Group) : null)
                .Where(g => g != null).GroupBy(g => g.Id).Select(g => g.First()).ToList();
            List<Group> naVista = new FilteredElementCollector(Doc, Doc.ActiveView.Id).OfClass(typeof(Group)).Cast<Group>().Where(g => g.GroupId == ElementId.InvalidElementId).ToList();
            List<Group> todos = new FilteredElementCollector(Doc).OfClass(typeof(Group)).Cast<Group>().Where(g => g.GroupId == ElementId.InvalidElementId).ToList();
            if (todos.Count == 0) { Show("O projeto não tem grupos.", ResultKind.Info); return Result.Cancelled; }

            var dlg = new OptionsDialog(Title, "SuspenderGrupos", "Suspender");
            var fScope = dlg.Choice("scope", "Grupos", new[] { $"Selecionados ({selecionados.Count})", $"Na vista ativa ({naVista.Count})", $"Todo o projeto ({todos.Count})" },
                selecionados.Count > 0 ? 0 : 1);
            if (selecionados.Count > 0) fScope.Index = 0;
            var fDetail = dlg.Check("detail", "Incluir grupos de detalhe", true);
            dlg.Note("Depois do OK: os elementos dos grupos ficam soltos para editar um a um. Clique de novo no botão (agora “Restaurar Grupos”) para juntá-los outra vez — mesmo depois de salvar e reabrir o projeto.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            List<Group> alvo = fScope.Index == 0 ? selecionados : fScope.Index == 1 ? naVista : todos;
            if (!fDetail.Value) alvo = alvo.Where(g => !Detalhe(g)).ToList();
            if (alvo.Count == 0) { Show("Nenhum grupo para suspender com essas opções.", ResultKind.Info); return Result.Cancelled; }

            var problems = new List<string>();
            int n = 0, anexados = 0;
            ToolProgress.Setup("Suspendendo " + alvo.Count + " grupo(s)", new[] { "Desagrupar", "Concluir" });
            using (var t = new Transaction(Doc, "JA Tools - Suspender grupos"))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                for (int i = 0; i < alvo.Count; i++)
                {
                    Group g = alvo[i];
                    if (!g.IsValidObject) continue;
                    if (i % 10 == 0) ToolProgress.Report(i, alvo.Count, g.Name);
                    try
                    {
                        if (TemDetalheAnexado(g)) { anexados++; continue; }
                        GroupType tipo = g.GroupType;
                        List<ElementId> membros = g.GetMemberIds().ToList();
                        var rec = new GrupoRegistro
                        {
                            TipoUid = tipo.UniqueId,
                            TipoNome = tipo.Name,
                            Detalhe = Detalhe(g),
                            Fixado = g.Pinned,
                            Copias = tipo.Groups?.Size ?? 1,
                            Assinatura = Assinatura(Doc, membros)
                        };
                        XYZ c = Centro(Doc, membros);
                        rec.Centro = c == null ? null : new[] { c.X, c.Y, c.Z };
                        if (g.Pinned) g.Pinned = false;
                        ICollection<ElementId> soltos = g.UngroupMembers();
                        rec.Membros = soltos.Select(id => Doc.GetElement(id)?.UniqueId).Where(u => u != null).ToList();
                        est.Grupos.Add(rec);
                        n++;
                    }
                    catch (Exception ex) { RevitUtil.AddOnce(problems, $"{g.Name}: {ex.Message}"); }
                }
                GruposSuspensos.Gravar(Doc, est);
                t.Commit();
            }
            Faixa.Atualizar(Doc);

            if (anexados > 0) problems.Insert(0, $"{anexados} grupo(s) com grupo de detalhe anexado ficaram como estavam (o Revit apagaria os detalhes anexados).");
            if (problems.Count == 0) return Result.Succeeded;   // quiet: the button now says "Restaurar Grupos"
            Show($"{n} grupo(s) suspensos.\n\nAvisos:\n" + string.Join("\n", problems.Take(15).Select(p => "  • " + p)), ResultKind.Warning);
            return Result.Succeeded;
        }

        // ================================================================== restore

        private Result Restaurar(Estado est)
        {
            int alterados = est.Grupos.Count(r => !Igual(Assinatura(Doc, Ids(r)), r.Assinatura));
            var dlg = new OptionsDialog("Suspender Grupos", "RestaurarGrupos", "Restaurar grupos");
            var fWhat = dlg.Choice("what", "Grupos suspensos", new[] { $"Restaurar os {est.Grupos.Count}", "Esquecer (ficam desagrupados)" }, 0,
                "Esquecer: os elementos continuam soltos e o botão volta a “Suspender Grupos”.");
            dlg.Note($"{est.Grupos.Count} grupo(s) suspenso(s); {alterados} com elementos alterados. " +
                     "Os não alterados voltam ao mesmo tipo (as cópias continuam cópias). Um grupo alterado vira um tipo novo “… (editado)” — " +
                     "ou mantém o nome quando era a única cópia do tipo.");
            if (!dlg.Show(Owner)) return Result.Cancelled;

            if (fWhat.Index == 1)
            {
                using (var t = new Transaction(Doc, "JA Tools - Esquecer grupos suspensos"))
                {
                    t.Start();
                    GruposSuspensos.Gravar(Doc, new Estado());
                    t.Commit();
                }
                Faixa.Atualizar(Doc);
                return Result.Succeeded;
            }

            var problems = new List<string>();
            int iguais = 0, editados = 0, renomeados = 0, vazios = 0;
            var fixar = new List<Group>();
            ToolProgress.Setup("Restaurando " + est.Grupos.Count + " grupo(s)", new[] { "Reagrupar", "Concluir" });
            using (var t = new Transaction(Doc, "JA Tools - Restaurar grupos"))
            {
                t.Start();
                RevitDwgUnifier.Export.FailureSwallower.Attach(t);
                int k = 0;
                foreach (var porTipo in est.Grupos.GroupBy(r => r.TipoUid))
                {
                    GroupType original = Doc.GetElement(porTipo.Key) as GroupType;
                    string nome = porTipo.First().TipoNome;
                    var feitos = new List<(GrupoRegistro rec, Group g, bool igual)>();

                    foreach (GrupoRegistro rec in porTipo)
                    {
                        ToolProgress.Report(k++, est.Grupos.Count, nome);
                        List<ElementId> ids = Ids(rec);
                        if (ids.Count == 0) { vazios++; continue; }
                        try
                        {
                            bool igual = Igual(Assinatura(Doc, ids), rec.Assinatura);
                            Group g = Doc.Create.NewGroup(ids);
                            feitos.Add((rec, g, igual));
                        }
                        catch (Exception ex) { RevitUtil.AddOnce(problems, $"{nome}: {ex.Message}"); }
                    }
                    if (feitos.Count == 0) continue;

                    // the type everything unchanged goes back to: the original, or — when nothing else
                    // uses it — the first unchanged group's own new type takes its name (elements keep their ids)
                    GroupType basis = original;
                    bool emUso = original != null && (original.Groups?.Size ?? 0) > 0;
                    if (!emUso)
                    {
                        var primeiro = feitos.FirstOrDefault(f => f.igual).g ?? feitos[0].g;
                        if (primeiro != null)
                        {
                            GroupType novo = primeiro.GroupType;
                            if (original != null) { try { Doc.Delete(original.Id); } catch { } }
                            Renomear(novo, nome);
                            basis = novo;
                            if (feitos.First(f => f.g.Id == primeiro.Id).igual) iguais++; else editados++;
                            if (feitos.First(f => f.g.Id == primeiro.Id).rec.Fixado) fixar.Add(primeiro);
                            renomeados++;
                            feitos.RemoveAll(f => f.g.Id == primeiro.Id);
                        }
                    }

                    foreach (var f in feitos)
                    {
                        if (f.rec.Fixado) fixar.Add(f.g);
                        GroupType proprio = f.g.GroupType;
                        if (f.igual && basis != null && basis.Id != proprio.Id && TrocarTipo(f.g, basis, f.rec))
                        {
                            try { if ((proprio.Groups?.Size ?? 0) == 0) Doc.Delete(proprio.Id); } catch { }
                            iguais++;
                        }
                        else
                        {
                            Renomear(proprio, nome + (f.igual ? " (restaurado)" : " (editado)"));
                            if (f.igual) RevitUtil.AddOnce(problems, $"{nome}: uma cópia não pôde voltar ao tipo original (espelhada?) e ficou como “{proprio.Name}”.");
                            else editados++;
                        }
                    }
                }
                foreach (Group gp in fixar) { try { if (gp.IsValidObject) gp.Pinned = true; } catch { } }
                GruposSuspensos.Gravar(Doc, new Estado());
                t.Commit();
            }
            Faixa.Atualizar(Doc);

            if (problems.Count == 0 && editados == 0 && vazios == 0) return Result.Succeeded;
            var sb = new StringBuilder();
            sb.AppendLine($"{iguais + editados} grupo(s) restaurado(s).");
            if (editados > 0) sb.AppendLine($"{editados} alterado(s): viraram tipo novo “(editado)”, a não ser quando eram a única cópia do tipo (mantêm o nome).");
            if (vazios > 0) sb.AppendLine($"{vazios} grupo(s) não voltaram: todos os elementos foram apagados.");
            if (problems.Count > 0) sb.AppendLine().AppendLine("Avisos:").AppendLine(string.Join("\n", problems.Take(15).Select(p => "  • " + p)));
            Show(sb.ToString().TrimEnd(), problems.Count > 0 ? ResultKind.Warning : ResultKind.Success);
            return Result.Succeeded;
        }

        /// <summary>Puts the regrouped elements back as a copy of <paramref name="tipo"/> in the same
        /// place: swap the type, then move / turn / mirror until the geometry matches the signature.</summary>
        private bool TrocarTipo(Group g, GroupType tipo, GrupoRegistro rec)
        {
            XYZ c = rec.Centro != null && rec.Centro.Length == 3 ? new XYZ(rec.Centro[0], rec.Centro[1], rec.Centro[2]) : null;
            using (var st = new SubTransaction(Doc))
            {
                st.Start();
                try
                {
                    g.GroupType = tipo;
                    Doc.Regenerate();
                    if (Confere(g, rec)) { st.Commit(); return true; }
                    if (c != null)
                    {
                        XYZ now = Centro(Doc, g.GetMemberIds());
                        if (now != null && !now.IsAlmostEqualTo(c)) { ElementTransformUtils.MoveElement(Doc, g.Id, c - now); Doc.Regenerate(); }
                        if (Confere(g, rec)) { st.Commit(); return true; }
                        Line eixo = Line.CreateBound(c, c + XYZ.BasisZ);
                        foreach (bool espelhar in new[] { false, true })
                        {
                            if (espelhar) { ElementTransformUtils.MirrorElements(Doc, new[] { g.Id }, Plane.CreateByNormalAndOrigin(XYZ.BasisX, c), false); Doc.Regenerate(); Recentrar(g, c); }
                            for (int q = 0; q < 4; q++)
                            {
                                if (q > 0 || espelhar) { if (q > 0) { ElementTransformUtils.RotateElement(Doc, g.Id, eixo, Math.PI / 2); Doc.Regenerate(); Recentrar(g, c); } }
                                if (Confere(g, rec)) { st.Commit(); return true; }
                            }
                            ElementTransformUtils.RotateElement(Doc, g.Id, eixo, Math.PI / 2);   // back to the start of the turn
                            Doc.Regenerate();
                        }
                    }
                }
                catch (Exception ex) { Logger.Warn("[JA Tools] Restaurar grupo: " + ex.Message); }
                st.RollBack();
            }
            return false;
        }

        private void Recentrar(Group g, XYZ c)
        {
            XYZ now = Centro(Doc, g.GetMemberIds());
            if (now != null && !now.IsAlmostEqualTo(c)) { ElementTransformUtils.MoveElement(Doc, g.Id, c - now); Doc.Regenerate(); }
        }

        private bool Confere(Group g, GrupoRegistro rec) => Igual(Assinatura(Doc, g.GetMemberIds()), rec.Assinatura);

        // ================================================================== helpers

        private List<ElementId> Ids(GrupoRegistro r) =>
            r.Membros.Select(u => Doc.GetElement(u)).Where(e => e != null && e.GroupId == ElementId.InvalidElementId).Select(e => e.Id).ToList();

        private static Group Topo(Group g)
        {
            while (g != null && g.GroupId != ElementId.InvalidElementId) g = g.Document.GetElement(g.GroupId) as Group;
            return g;
        }

        private static bool Detalhe(Group g) => g.Category != null && g.Category.Id.Value == (long)BuiltInCategory.OST_IOSDetailGroups;

        private static bool TemDetalheAnexado(Group g)
        {
            try { return g.GroupType != null && g.GroupType.GetAvailableAttachedDetailGroupTypeIds().Count > 0; } catch { return false; }
        }

        private static void Renomear(GroupType t, string nome)
        {
            if (t == null || t.Name == nome) return;
            var usados = new HashSet<string>(new FilteredElementCollector(t.Document).OfClass(typeof(GroupType)).Cast<GroupType>()
                .Where(x => x.Id != t.Id).Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
            string n = nome;
            for (int i = 2; usados.Contains(n); i++) n = nome + " " + i;
            try { t.Name = n; } catch { }
        }

        /// <summary>Category, type and box (mm) of every member, sorted: equal lists = same geometry.</summary>
        public static List<string> Assinatura(Document doc, IEnumerable<ElementId> ids)
        {
            var list = new List<string>();
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                if (e == null) continue;
                BoundingBoxXYZ bb = Caixa(doc, e);
                string box = bb == null ? "-" : R(bb.Min) + "/" + R(bb.Max);
                list.Add((e.Category?.Id.Value ?? 0) + "|" + e.GetTypeId().Value + "|" + box);
            }
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static bool Igual(List<string> a, List<string> b) => a != null && b != null && a.SequenceEqual(b);

        private static BoundingBoxXYZ Caixa(Document doc, Element e)
        {
            BoundingBoxXYZ bb = null;
            try { bb = e.get_BoundingBox(null); } catch { }
            if (bb == null && e.OwnerViewId != ElementId.InvalidElementId && doc.GetElement(e.OwnerViewId) is View v)
            {
                try { bb = e.get_BoundingBox(v); } catch { }
            }
            return bb;
        }

        private static XYZ Centro(Document doc, IEnumerable<ElementId> ids)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, z0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue, z1 = double.MinValue;
            bool any = false;
            foreach (ElementId id in ids)
            {
                Element e = doc.GetElement(id);
                BoundingBoxXYZ bb = e == null ? null : Caixa(doc, e);
                if (bb == null) continue;
                any = true;
                x0 = Math.Min(x0, bb.Min.X); y0 = Math.Min(y0, bb.Min.Y); z0 = Math.Min(z0, bb.Min.Z);
                x1 = Math.Max(x1, bb.Max.X); y1 = Math.Max(y1, bb.Max.Y); z1 = Math.Max(z1, bb.Max.Z);
            }
            return any ? new XYZ((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2) : null;
        }

        private static string R(XYZ p) =>
            string.Join(",", new[] { p.X, p.Y, p.Z }.Select(v => Math.Round(v * 304.8).ToString("0", CultureInfo.InvariantCulture)));
    }

    // ====================================================================== state in the model

    [DataContract]
    public sealed class GrupoRegistro
    {
        [DataMember(Order = 1)] public string TipoUid { get; set; }
        [DataMember(Order = 2)] public string TipoNome { get; set; }
        [DataMember(Order = 3)] public bool Detalhe { get; set; }
        [DataMember(Order = 4)] public bool Fixado { get; set; }
        [DataMember(Order = 5)] public int Copias { get; set; }
        [DataMember(Order = 6)] public List<string> Membros { get; set; } = new List<string>();
        [DataMember(Order = 7)] public List<string> Assinatura { get; set; } = new List<string>();
        [DataMember(Order = 8)] public double[] Centro { get; set; }
    }

    [DataContract]
    public sealed class Estado
    {
        [DataMember(Order = 1)] public List<GrupoRegistro> Grupos { get; set; } = new List<GrupoRegistro>();
    }

    /// <summary>The suspended groups of a document, kept in extensible storage (one DataStorage).</summary>
    public static class GruposSuspensos
    {
        private static readonly Guid SchemaId = new Guid("5d2b9c1e-7a43-4f0e-9b6d-3e8a1f4c2d77");

        private static Schema Esquema()
        {
            Schema s = Schema.Lookup(SchemaId);
            if (s != null) return s;
            var b = new SchemaBuilder(SchemaId);
            b.SetSchemaName("JATools_GruposSuspensos");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField("Json", typeof(string));
            return b.Finish();
        }

        private static DataStorage Armazem(Document doc, Schema s) =>
            new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>()
                .FirstOrDefault(d => { try { return d.GetEntity(s).IsValid(); } catch { return false; } });

        public static Estado Ler(Document doc)
        {
            try
            {
                Schema s = Schema.Lookup(SchemaId);
                if (s == null) return new Estado();
                DataStorage ds = Armazem(doc, s);
                if (ds == null) return new Estado();
                string json = ds.GetEntity(s).Get<string>("Json");
                if (string.IsNullOrWhiteSpace(json)) return new Estado();
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    return (Estado)new DataContractJsonSerializer(typeof(Estado)).ReadObject(ms) ?? new Estado();
            }
            catch (Exception ex) { Logger.Warn("[JA Tools] Grupos suspensos ilegíveis: " + ex.Message); return new Estado(); }
        }

        /// <summary>Inside a transaction. An empty state removes the storage.</summary>
        public static void Gravar(Document doc, Estado est)
        {
            Schema s = Esquema();
            DataStorage ds = Armazem(doc, s);
            if (est == null || est.Grupos.Count == 0)
            {
                if (ds != null) { try { doc.Delete(ds.Id); } catch { } }
                return;
            }
            if (ds == null) ds = DataStorage.Create(doc);
            string json;
            using (var ms = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(Estado)).WriteObject(ms, est);
                json = Encoding.UTF8.GetString(ms.ToArray());
            }
            var ent = new Entity(s);
            ent.Set("Json", json);
            ds.SetEntity(ent);
        }

        public static int Quantos(Document doc)
        {
            try { return doc == null || doc.IsFamilyDocument ? 0 : Ler(doc).Grupos.Count; } catch { return 0; }
        }
    }

    /// <summary>The ribbon button says what one click will do: "Suspender Grupos" or "Restaurar Grupos (n)".</summary>
    public static class Faixa
    {
        public static PushButton Botao;

        public static void Atualizar(Document doc)
        {
            if (Botao == null) return;
            try
            {
                int n = GruposSuspensos.Quantos(doc);
                Botao.ItemText = n > 0 ? "Restaurar\nGrupos (" + n + ")" : "Suspender\nGrupos";
                Botao.ToolTip = n > 0
                    ? $"{n} grupo(s) suspenso(s) neste projeto: os elementos estão soltos. Clique para juntá-los de novo."
                    : "Desfaz os grupos temporariamente (como “Suspender grupos” do ArchiCAD) para editar cada elemento; clique de novo para restaurar.";
            }
            catch { }
        }
    }
}
