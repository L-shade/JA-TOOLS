using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace JAEsquadrias.Engine
{
    /// <summary>
    /// "Quadro de Esquadrias": a schedule per category with the JA esquadrias only (filter on
    /// JA_Abertura having a value), one line per type (code), with count and the JA data.
    /// </summary>
    public static class ScheduleBuilder
    {
        public static ViewSchedule Create(Document doc, BuiltInCategory cat, string name, bool recreate, List<string> warnings, bool elevacao = false)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(v => v.Name == name);
            if (existing != null)
            {
                if (!recreate) return existing;
                doc.Delete(existing.Id);
            }

            ViewSchedule vs = ViewSchedule.CreateSchedule(doc, new ElementId(cat));
            try { vs.Name = name; } catch (Exception ex) { warnings.Add("Nome do quadro não aplicado: " + ex.Message); }
            ScheduleDefinition def = vs.Definition;
            def.IsItemized = false;
            IList<SchedulableField> fields = def.GetSchedulableFields();
            bool janela = cat == BuiltInCategory.OST_Windows;

            ScheduleField Add(Func<SchedulableField, bool> match, string heading, string label)
            {
                SchedulableField sf = fields.FirstOrDefault(match);
                if (sf == null) { warnings.Add($"Campo “{label}” não disponível no quadro de {(janela ? "janelas" : "portas")}."); return null; }
                ScheduleField f = def.AddField(sf);
                if (heading != null) f.ColumnHeading = heading;
                return f;
            }
            bool Bip(SchedulableField sf, params BuiltInParameter[] bips) => bips.Any(b => sf.ParameterId == new ElementId(b));
            bool Shared(SchedulableField sf, SharedParams.Def d)
            {
                ElementId id = SharedParams.ProjectId(doc, d);
                return id != ElementId.InvalidElementId && sf.ParameterId == id;
            }

            ScheduleField mark = Add(sf => Bip(sf, BuiltInParameter.ALL_MODEL_TYPE_MARK), "Código", "Marca de tipo");
            if (elevacao) Add(sf => Bip(sf, BuiltInParameter.ALL_MODEL_TYPE_IMAGE), "Elevação", "Imagem do tipo");
            Add(sf => Bip(sf, BuiltInParameter.ALL_MODEL_DESCRIPTION), "Descrição", "Descrição");
            Add(sf => Bip(sf, BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.DOOR_WIDTH), "Largura", "Largura");
            Add(sf => Bip(sf, BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.DOOR_HEIGHT), "Altura", "Altura");
            if (janela) Add(sf => Bip(sf, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM), "Peitoril", "Altura do peitoril");
            Add(sf => sf.FieldType == ScheduleFieldType.Count, "Qtd.", "Contador");
            Add(sf => Shared(sf, SharedParams.Abertura), "Abertura", "JA_Abertura");
            Add(sf => Shared(sf, SharedParams.Linha), "Perfil", "JA_Linha");
            Add(sf => Shared(sf, SharedParams.Cor), "Cor", "JA_Cor");
            Add(sf => Shared(sf, SharedParams.Vidro), "Vidro", "JA_Vidro");
            Add(sf => Shared(sf, SharedParams.Ferragens), "Ferragens", "JA_Ferragens");
            Add(sf => Shared(sf, SharedParams.AreaIluminacao), "Iluminação", "JA_AreaIluminacao");
            Add(sf => Shared(sf, SharedParams.AreaVentilacao), "Ventilação", "JA_AreaVentilacao");
            Add(sf => Shared(sf, SharedParams.Uw), "Uw (W/m²K)", "JA_Uw");
            Add(sf => Shared(sf, SharedParams.SoleiraCodigo), janela ? "Peitoril" : "Soleira", "JA_SoleiraCodigo");
            Add(sf => Shared(sf, SharedParams.SoleiraMaterial), janela ? "Material do peitoril" : "Material da soleira", "JA_SoleiraMaterial");

            // only JA esquadrias
            ScheduleField filterField = def.GetFieldOrder().Select(id => def.GetField(id)).FirstOrDefault(f => f.ParameterId == SharedParams.ProjectId(doc, SharedParams.Abertura));
            if (filterField != null)
            {
                try { def.AddFilter(new ScheduleFilter(filterField.FieldId, ScheduleFilterType.HasValue)); }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Filtro do quadro: " + ex.Message); }
            }
            if (mark != null)
            {
                try { def.AddSortGroupField(new ScheduleSortGroupField(mark.FieldId)); }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Ordenação do quadro: " + ex.Message); }
            }
            return vs;
        }

        /// <summary>
        /// "Quadro de Soleiras e Peitoris": doors and windows together, one line per stone code,
        /// with material, size and quantity (for the stone supplier).
        /// </summary>
        public static ViewSchedule CreateStones(Document doc, string name, bool recreate, List<string> warnings)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(v => v.Name == name);
            if (existing != null)
            {
                if (!recreate) return existing;
                doc.Delete(existing.Id);
            }
            ViewSchedule vs = ViewSchedule.CreateSchedule(doc, ElementId.InvalidElementId);
            try { vs.Name = name; } catch (Exception ex) { warnings.Add("Nome do quadro não aplicado: " + ex.Message); }
            ScheduleDefinition def = vs.Definition;
            def.IsItemized = false;
            IList<SchedulableField> fields = def.GetSchedulableFields();

            ScheduleField Add(SharedParams.Def d, string heading)
            {
                ElementId id = SharedParams.ProjectId(doc, d);
                SchedulableField sf = id == ElementId.InvalidElementId ? null : fields.FirstOrDefault(f => f.ParameterId == id);
                if (sf == null) { warnings.Add($"Campo “{d.Name}” não disponível (gere uma esquadria com soleira/peitoril primeiro)."); return null; }
                ScheduleField f2 = def.AddField(sf);
                f2.ColumnHeading = heading;
                return f2;
            }

            ScheduleField code = Add(SharedParams.SoleiraCodigo, "Código");
            Add(SharedParams.SoleiraDimensoes, "Peça e dimensões (C x L x E)");
            Add(SharedParams.SoleiraMaterial, "Material");
            SchedulableField count = fields.FirstOrDefault(f => f.FieldType == ScheduleFieldType.Count);
            if (count != null) def.AddField(count).ColumnHeading = "Qtd.";
            if (code != null)
            {
                try { def.AddFilter(new ScheduleFilter(code.FieldId, ScheduleFilterType.HasValue)); } catch (Exception ex) { Logger.Warn("[JA Famílias] Filtro do quadro de soleiras: " + ex.Message); }
                try { def.AddSortGroupField(new ScheduleSortGroupField(code.FieldId)); } catch (Exception ex) { Logger.Warn("[JA Famílias] Ordenação do quadro de soleiras: " + ex.Message); }
            }
            return vs;
        }

        /// <summary>
        /// "Quadro de Bancadas / Nichos / Chapins": Generic Models of one JA kind (filter on
        /// JA_Familia), one line per code, with description, dimensions, material, stone area and
        /// quantity; chapins with the total length.
        /// </summary>
        public static ViewSchedule CreatePecas(Document doc, Core.TipoPeca tipo, string name, bool recreate, List<string> warnings)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(v => v.Name == name);
            if (existing != null)
            {
                if (!recreate) return existing;
                doc.Delete(existing.Id);
            }
            // bancadas: Generic Models; nichos and chapins: Casework
            ViewSchedule vs = ViewSchedule.CreateSchedule(doc, new ElementId(Core.PecaUtil.Tubo(tipo) ? BuiltInCategory.OST_PipeFitting
                                                                     : Core.PecaUtil.Gabinete(tipo) ? BuiltInCategory.OST_Casework : BuiltInCategory.OST_GenericModel));
            try { vs.Name = name; } catch (Exception ex) { warnings.Add("Nome do quadro não aplicado: " + ex.Message); }
            ScheduleDefinition def = vs.Definition;
            def.IsItemized = false;
            try { def.ShowGrandTotal = true; def.ShowGrandTotalCount = true; def.ShowGrandTotalTitle = true; } catch { }
            IList<SchedulableField> fields = def.GetSchedulableFields();
            string kind = Core.PecaUtil.NomeTipo(tipo);

            ScheduleField Add(Func<SchedulableField, bool> match, string heading, string label)
            {
                SchedulableField sf = fields.FirstOrDefault(match);
                if (sf == null) { warnings.Add($"Campo “{label}” não disponível no quadro de {kind.ToLowerInvariant()}s."); return null; }
                ScheduleField f = def.AddField(sf);
                if (heading != null) f.ColumnHeading = heading;
                return f;
            }
            bool Bip(SchedulableField sf, BuiltInParameter b) => sf.ParameterId == new ElementId(b);
            bool Shared(SchedulableField sf, SharedParams.Def d)
            {
                ElementId id = SharedParams.ProjectId(doc, d);
                return id != ElementId.InvalidElementId && sf.ParameterId == id;
            }
            void Totals(ScheduleField f)
            {
                if (f == null) return;
                try { f.DisplayType = ScheduleFieldDisplayType.Totals; } catch (Exception ex) { Logger.Warn("[JA Famílias] Totais: " + ex.Message); }
            }

            ScheduleField mark = Add(sf => Bip(sf, BuiltInParameter.ALL_MODEL_TYPE_MARK), "Código", "Marca de tipo");
            Add(sf => Bip(sf, BuiltInParameter.ALL_MODEL_DESCRIPTION), "Descrição", "Descrição");
            Add(sf => Shared(sf, SharedParams.Dimensoes), "Dimensões", "JA_Dimensoes");
            Add(sf => Shared(sf, SharedParams.Material), "Material", "JA_Material");
            Add(sf => sf.FieldType == ScheduleFieldType.Count, "Qtd.", "Contador");
            if (tipo == Core.TipoPeca.Chapim)
            {
                Totals(Add(sf => Shared(sf, SharedParams.Comprimento), "Comprimento total", "JA_Comprimento"));
                Add(sf => Shared(sf, SharedParams.AreaPedra), "Pedra por metro", "JA_AreaPedra");
            }
            else if (tipo == Core.TipoPeca.Vidro) Totals(Add(sf => Shared(sf, SharedParams.Comprimento), "Comprimento total", "JA_Comprimento"));
            else if (tipo == Core.TipoPeca.Madeira) Totals(Add(sf => Shared(sf, SharedParams.AreaPedra), "Área (painel / coberta)", "JA_AreaPedra"));
            else if (!Core.PecaUtil.Tubo(tipo))
            {
                Totals(Add(sf => Shared(sf, SharedParams.AreaPedra), "Área de pedra", "JA_AreaPedra"));
                // rodapés (line-based) next to soleiras and degraus
                if (tipo == Core.TipoPeca.Pedra) Totals(Add(sf => Shared(sf, SharedParams.Comprimento), "Comprimento total", "JA_Comprimento"));
            }
            // bancadas with wet areas (only when some bancada of the project has one)
            if (tipo == Core.TipoPeca.Bancada && fields.Any(sf => Shared(sf, SharedParams.AreaMolhada)))
                Totals(Add(sf => Shared(sf, SharedParams.AreaMolhada), "Área molhada", "JA_AreaMolhada"));

            ScheduleField kindField = Add(sf => Shared(sf, SharedParams.Familia), null, "JA_Familia");
            if (kindField != null)
            {
                kindField.IsHidden = true;
                try { def.AddFilter(new ScheduleFilter(kindField.FieldId, ScheduleFilterType.Equal, kind)); }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Filtro do quadro de peças: " + ex.Message); }
            }
            if (mark != null)
            {
                try { def.AddSortGroupField(new ScheduleSortGroupField(mark.FieldId)); }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Ordenação do quadro de peças: " + ex.Message); }
            }
            return vs;
        }

        /// <summary>
        /// "Quadro de Painéis de Cortina": JA curtain panels (JA_Familia = "JA Painel"), one line
        /// per code and size, with description, dimensions, material, quantity and total area.
        /// </summary>
        public static ViewSchedule CreatePaineis(Document doc, string name, bool recreate, List<string> warnings)
        {
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault(v => v.Name == name);
            if (existing != null)
            {
                if (!recreate) return existing;
                doc.Delete(existing.Id);
            }
            ViewSchedule vs = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_CurtainWallPanels));
            try { vs.Name = name; } catch (Exception ex) { warnings.Add("Nome do quadro não aplicado: " + ex.Message); }
            ScheduleDefinition def = vs.Definition;
            def.IsItemized = false;
            try { def.ShowGrandTotal = true; def.ShowGrandTotalCount = true; def.ShowGrandTotalTitle = true; } catch { }
            IList<SchedulableField> fields = def.GetSchedulableFields();

            ScheduleField Add(Func<SchedulableField, bool> match, string heading, string label)
            {
                SchedulableField sf = fields.FirstOrDefault(match);
                if (sf == null) { warnings.Add($"Campo “{label}” não disponível no quadro de painéis."); return null; }
                ScheduleField f = def.AddField(sf);
                if (heading != null) f.ColumnHeading = heading;
                return f;
            }
            bool Bip(SchedulableField sf, BuiltInParameter b) => sf.ParameterId == new ElementId(b);
            bool Shared(SchedulableField sf, SharedParams.Def d)
            {
                ElementId id = SharedParams.ProjectId(doc, d);
                return id != ElementId.InvalidElementId && sf.ParameterId == id;
            }

            ScheduleField mark = Add(sf => Bip(sf, BuiltInParameter.ALL_MODEL_TYPE_MARK), "Código", "Marca de tipo");
            Add(sf => Bip(sf, BuiltInParameter.ALL_MODEL_DESCRIPTION), "Descrição", "Descrição");
            ScheduleField dims = Add(sf => Shared(sf, SharedParams.Dimensoes), "Dimensões", "JA_Dimensoes");
            Add(sf => Shared(sf, SharedParams.Material), "Material", "JA_Material");
            Add(sf => sf.FieldType == ScheduleFieldType.Count, "Qtd.", "Contador");
            ScheduleField area = Add(sf => Bip(sf, BuiltInParameter.HOST_AREA_COMPUTED), "Área", "Área");
            if (area != null)
            {
                try { area.DisplayType = ScheduleFieldDisplayType.Totals; } catch (Exception ex) { Logger.Warn("[JA Famílias] Totais: " + ex.Message); }
            }
            ScheduleField kindField = Add(sf => Shared(sf, SharedParams.Familia), null, "JA_Familia");
            if (kindField != null)
            {
                kindField.IsHidden = true;
                try { def.AddFilter(new ScheduleFilter(kindField.FieldId, ScheduleFilterType.Equal, PainelGenerator.Marca)); }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Filtro do quadro de painéis: " + ex.Message); }
            }
            foreach (ScheduleField f in new[] { mark, dims })
            {
                if (f == null) continue;
                try { def.AddSortGroupField(new ScheduleSortGroupField(f.FieldId)); }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Ordenação do quadro de painéis: " + ex.Message); }
            }
            return vs;
        }
    }
}
