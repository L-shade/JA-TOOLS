using System;
using System.Runtime.Serialization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using JAEsquadrias.Infrastructure;

namespace JAEsquadrias.Common
{
    /// <summary>What a JA tool left on an element it created (rodapé, paginação…), to find and redo it later.</summary>
    [DataContract]
    public sealed class DadosJa
    {
        [DataMember(Order = 1)] public string Tipo { get; set; }          // "rodape", "paginacao", "forro", "impermeabilizacao"
        [DataMember(Order = 2)] public long Ambiente { get; set; }        // room id (0 = none)
        [DataMember(Order = 3)] public long Vista { get; set; }
        [DataMember(Order = 4)] public string Face { get; set; }          // stable reference of a wall face (paginação de parede)
        [DataMember(Order = 5)] public string Config { get; set; }        // the tool's settings, as text
        [DataMember(Order = 6)] public double AreaM2 { get; set; }
        [DataMember(Order = 7)] public double CompraM2 { get; set; }
        [DataMember(Order = 8)] public string Peca { get; set; }          // "600×600"
        [DataMember(Order = 9)] public int Inteiras { get; set; }
        [DataMember(Order = 10)] public int Recortes { get; set; }
        [DataMember(Order = 11)] public double RejunteKg { get; set; }
        [DataMember(Order = 12)] public double ArgamassaKg { get; set; }
        [DataMember(Order = 13)] public double Caixas { get; set; }
        [DataMember(Order = 14)] public double Metros { get; set; }        // linear metres (tabica, sanca, rodapé, ripas)
        [DataMember(Order = 15)] public string Descricao { get; set; }     // what the element is, in words
    }

    /// <summary>Extensible storage (one text field) on elements created by the JA tools.</summary>
    public static class JaDados
    {
        private static readonly Guid Id = new Guid("3b6f2c1e-8a4d-4f7b-9c2e-5d1a7e9f0b42");

        private static Schema Esquema()
        {
            Schema s = Schema.Lookup(Id);
            if (s != null) return s;
            var b = new SchemaBuilder(Id);
            b.SetSchemaName("JAFamiliasDados");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField("Json", typeof(string));
            return b.Finish();
        }

        public static DadosJa Ler(Element e)
        {
            try
            {
                Schema s = Schema.Lookup(Id);
                if (s == null || e == null) return null;
                Entity en = e.GetEntity(s);
                if (!en.IsValid()) return null;
                string json = en.Get<string>("Json");
                return string.IsNullOrWhiteSpace(json) ? null : Json.Deserialize<DadosJa>(json);
            }
            catch { return null; }
        }

        /// <summary>Inside a transaction.</summary>
        public static void Gravar(Element e, DadosJa d)
        {
            try
            {
                var en = new Entity(Esquema());
                en.Set("Json", Json.Serialize(d));
                e.SetEntity(en);
            }
            catch (Exception ex) { Logger.Warn("[JA Famílias] Dados não gravados no elemento " + e?.Id.Value + ": " + ex.Message); }
        }
    }
}
