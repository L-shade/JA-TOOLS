using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitDwgUnifier.Infrastructure;
using RevitDwgUnifier.Tools.Common;

namespace RevitDwgUnifier.Tools.Views
{
    /// <summary>One side of a room: all its straight boundary pieces that face the same way.</summary>
    public sealed class LadoParede
    {
        /// <summary>Horizontal unit vector from the room toward this wall (the elevation looks this way).</summary>
        public XYZ Olhar;
        /// <summary>Clockwise from the room's "up" (its walls' frame), 0–360.</summary>
        public double Graus;
        /// <summary>0 = A (top of the plan), 1 = B (right), 2 = C (bottom), 3 = D (left).</summary>
        public int Quadrante;
        public string Letra = "";
        public double Comprimento;
        /// <summary>Plan points of its pieces (the crop width comes from them).</summary>
        public List<XYZ> Pontos = new List<XYZ>();
    }

    /// <summary>
    /// Views of a room parallel to its walls, whatever their angle: one elevation per wall direction
    /// (markers turned to face each wall; walls at 90° to each other share a marker), crops from the
    /// wall itself, and the frame (angle) to turn the plan and the isometric with the room.
    /// Shared by Vistas por Ambiente and Detalhar Ambientes.
    /// </summary>
    public static class ParedesAmbiente
    {
        private const double TolGraus = 3.0;
        private static readonly XYZ[] Cardeais = { XYZ.BasisY, XYZ.BasisX, -XYZ.BasisY, -XYZ.BasisX };

        public static XYZ Girar(XYZ v, double ang) =>
            new XYZ(v.X * Math.Cos(ang) - v.Y * Math.Sin(ang), v.X * Math.Sin(ang) + v.Y * Math.Cos(ang), v.Z);

        /// <summary>Angle (radians, −45°..45°) of the room walls: the direction, modulo 90°, that sums
        /// the longest boundary. 0 for rooms square to the plan.</summary>
        public static double AnguloDominante(Room r)
        {
            var pesos = new Dictionary<int, double>();   // half-degree buckets in [0, 90)
            foreach (var loop in RevitUtil.Boundary(r))
                foreach (BoundarySegment s in loop)
                {
                    if (!(s.GetCurve() is Line ln)) continue;
                    XYZ d = ln.Direction;
                    double a = Math.Atan2(d.Y, d.X) * 180 / Math.PI;
                    a = ((a % 90) + 90) % 90;
                    int b = (int)Math.Round(a * 2) % 180;
                    pesos[b] = (pesos.TryGetValue(b, out double w) ? w : 0) + ln.Length;
                }
            if (pesos.Count == 0) return 0;
            int best = pesos.OrderByDescending(kv => kv.Value).First().Key;
            double graus = best / 2.0;
            if (graus > 45) graus -= 90;
            if (Math.Abs(graus) < 0.26) return 0;
            return graus * Math.PI / 180;
        }

        /// <summary>
        /// The sides of the room: straight pieces of the outer boundary grouped by the way they face
        /// (±3°); a direction shorter than <paramref name="minimo"/> in total (chamfers, pilaster
        /// sides that face nowhere else) is left out. Letters clockwise from the top: A–D for a room
        /// with up to four sides (A top, B right, C bottom, D left), A, B, C, D, E… otherwise.
        /// </summary>
        public static List<LadoParede> Lados(Room r, double minimo)
        {
            var loops = RevitUtil.Boundary(r);
            if (loops == null || loops.Count == 0) return new List<LadoParede>();
            IList<BoundarySegment> externo = loops.OrderByDescending(l => l.Sum(s => s.GetCurve().Length)).First();

            // orientation of the loop: interior on the left of each piece when counter-clockwise
            double area = 0;
            foreach (BoundarySegment s in externo)
            {
                XYZ a = s.GetCurve().GetEndPoint(0), b = s.GetCurve().GetEndPoint(1);
                area += a.X * b.Y - b.X * a.Y;
            }
            bool antiHorario = area > 0;

            var lados = new List<LadoParede>();
            double cosTol = Math.Cos(TolGraus * Math.PI / 180);
            foreach (BoundarySegment s in externo)
            {
                if (!(s.GetCurve() is Line ln) || ln.Length < 1e-3) continue;
                XYZ d = ln.Direction;
                XYZ esquerda = new XYZ(-d.Y, d.X, 0).Normalize();
                XYZ olhar = antiHorario ? -esquerda : esquerda;
                LadoParede g = lados.FirstOrDefault(l => l.Olhar.DotProduct(olhar) > cosTol);
                if (g == null) { g = new LadoParede { Olhar = olhar }; lados.Add(g); }
                else
                {
                    XYZ m = g.Olhar * g.Comprimento + olhar * ln.Length;   // length-weighted direction
                    if (m.GetLength() > 1e-9) g.Olhar = m.Normalize();
                }
                g.Comprimento += ln.Length;
                g.Pontos.Add(ln.GetEndPoint(0));
                g.Pontos.Add(ln.GetEndPoint(1));
            }
            var fortes = lados.Where(l => l.Comprimento >= minimo).ToList();
            if (fortes.Count > 0) lados = fortes;

            double theta = AnguloDominante(r);
            foreach (LadoParede l in lados)
            {
                XYZ loc = Girar(l.Olhar, -theta);
                double g = Math.Atan2(loc.X, loc.Y) * 180 / Math.PI;   // clockwise from up
                l.Graus = (g % 360 + 360) % 360;
                l.Quadrante = (int)Math.Round(l.Graus / 90) % 4;
            }
            lados = lados.OrderBy(l => (l.Graus + 45) % 360).ToList();
            bool quatro = lados.Count <= 4 && lados.Select(l => l.Quadrante).Distinct().Count() == lados.Count;
            for (int i = 0; i < lados.Count; i++)
                lados[i].Letra = quatro ? "ABCD"[lados[i].Quadrante].ToString() : Letra(i);
            return lados;
        }

        /// <summary>The four sides square to the project (A up, B right, C down, D left), each with the
        /// whole room outline: the old behaviour, when "parallel to the walls" is off.</summary>
        public static List<LadoParede> LadosOrtogonais(Room r)
        {
            var pts = new List<XYZ>();
            foreach (var loop in RevitUtil.Boundary(r))
                foreach (BoundarySegment s in loop)
                    pts.AddRange(s.GetCurve().Tessellate());
            if (pts.Count == 0)
            {
                BoundingBoxXYZ rb = RevitUtil.RoomBox(r);
                if (rb != null) pts.AddRange(new[] { rb.Min, rb.Max, new XYZ(rb.Min.X, rb.Max.Y, 0), new XYZ(rb.Max.X, rb.Min.Y, 0) });
            }
            return Enumerable.Range(0, 4).Select(i => new LadoParede
            {
                Olhar = Cardeais[i], Graus = i * 90, Quadrante = i, Letra = "ABCD"[i].ToString(), Comprimento = 1, Pontos = pts
            }).ToList();
        }

        private static string Letra(int i) => i < 26 ? ((char)('A' + i)).ToString() : "A" + (i - 25);

        // ------------------------------------------------------------------ markers

        private static XYZ Plano(XYZ v)
        {
            var p = new XYZ(v.X, v.Y, 0);
            return p.GetLength() < 1e-9 ? XYZ.Zero : p.Normalize();
        }

        /// <summary>
        /// One elevation per side, each looking straight at its wall, whatever the angle. Every side
        /// gets its own marker at the room centre (they overlap, so the plan shows one circle with an
        /// arrow per wall) using only the marker's first elevation: Revit reports the direction of
        /// that one reliably, while the other indices of a fresh marker read wrong (1.27.0/1.27.1:
        /// sides at 90° from a measured one were refused). After one regenerate each marker is turned
        /// by the angle between its elevation and its wall.
        /// </summary>
        public static List<(LadoParede lado, ViewSection vista)> CriarElevacoes(Document doc, ElementId tipo, ElementId planoId, XYZ centro, int escala,
                                                                                 IEnumerable<LadoParede> lados, List<string> problemas, string titulo)
        {
            var feitas = new List<(LadoParede, ViewSection)>();
            var criados = new List<(ElevationMarker marker, int indice, ViewSection vista, LadoParede lado)>();
            foreach (LadoParede l in lados)
            {
                try
                {
                    ElevationMarker marker = ElevationMarker.CreateElevationMarker(doc, tipo, centro, escala);
                    ViewSection ev = null; int indice = -1;
                    for (int i = 0; i < 4 && ev == null; i++)
                        try { if (marker.IsAvailableIndex(i)) { ev = marker.CreateElevation(doc, planoId, i); indice = i; } } catch { }
                    if (ev == null) { try { doc.Delete(marker.Id); } catch { } throw new InvalidOperationException("o Revit não criou a elevação"); }
                    criados.Add((marker, indice, ev, l));
                }
                catch (Exception ex) { RevitUtil.AddOnce(problemas, $"{titulo} {l.Letra}: marcador de elevação ({ex.Message})."); }
            }
            if (criados.Count == 0) return feitas;
            doc.Regenerate();

            foreach (var (marker, indice, ev, l) in criados)
            {
                XYZ dir = Plano(-ev.ViewDirection);
                if (dir.GetLength() < 0.5) dir = Girar(-XYZ.BasisX, -indice * Math.PI / 2);   // 0 west, 1 north, 2 east, 3 south
                double giro = Math.Atan2(dir.CrossProduct(l.Olhar).Z, dir.DotProduct(l.Olhar));
                try
                {
                    if (Math.Abs(giro) > 1e-6)
                        ElementTransformUtils.RotateElement(doc, marker.Id, Line.CreateBound(centro, centro + XYZ.BasisZ), giro);
                    feitas.Add((l, ev));
                }
                catch (Exception ex)
                {
                    RevitUtil.AddOnce(problemas, $"{titulo} {l.Letra}: não foi possível pôr a elevação de frente para a parede ({ex.Message}).");
                    try { doc.Delete(marker.Id); } catch { }
                }
            }
            return feitas;
        }

        /// <summary>Crop = the wall seen from the front (+margin), floor to top; far clip just behind
        /// the wall. After a regenerate.</summary>
        public static void Recortar(ViewSection ev, LadoParede lado, double zMin, double zMax, double margem, bool mostrar)
        {
            BoundingBoxXYZ cb = ev.CropBox;
            Transform inv = cb.Transform.Inverse;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, depth = 0;
            XYZ look = -ev.ViewDirection;
            foreach (XYZ p in lado.Pontos)
                foreach (double z in new[] { zMin, zMax })
                {
                    var c = new XYZ(p.X, p.Y, z);
                    XYZ l = inv.OfPoint(c);
                    minX = Math.Min(minX, l.X); maxX = Math.Max(maxX, l.X);
                    minY = Math.Min(minY, l.Y); maxY = Math.Max(maxY, l.Y);
                    depth = Math.Max(depth, (c - ev.Origin).DotProduct(look));
                }
            if (minX > maxX) return;
            ev.CropBoxActive = true;
            ev.CropBox = new BoundingBoxXYZ
            {
                Transform = cb.Transform,
                Min = new XYZ(minX - margem, minY - margem, cb.Min.Z),
                Max = new XYZ(maxX + margem, maxY + margem, cb.Max.Z)
            };
            try { ev.CropBoxVisible = mostrar; } catch { }
            try
            {
                ev.get_Parameter(BuiltInParameter.VIEWER_BOUND_FAR_CLIPPING)?.Set(2);   // clip without line
                ev.get_Parameter(BuiltInParameter.VIEWER_BOUND_OFFSET_FAR)?.Set(depth + RevitUtil.M(0.3));
            }
            catch { }
        }

        // ------------------------------------------------------------------ plan turned with the room

        /// <summary>The crop region element of a view (found by deleting the view in a rolled-back
        /// sub-transaction: Revit lists the crop region among what would go with it).</summary>
        public static ElementId ElementoDoRecorte(Document doc, View v)
        {
            try
            {
                ICollection<ElementId> ids;
                using (var st = new SubTransaction(doc))
                {
                    st.Start();
                    ids = doc.Delete(v.Id);
                    st.RollBack();
                }
                foreach (ElementId id in ids)
                {
                    Element e = doc.GetElement(id);
                    if (e?.Category != null && e.Category.Id.Value == (long)BuiltInCategory.OST_CropBoundary) return id;
                }
            }
            catch (Exception ex) { Logger.Info("[JA Tools] Recorte da vista: " + ex.Message); }
            return ElementId.InvalidElementId;
        }

        /// <summary>
        /// Turns the plan's crop region by <paramref name="ang"/> (the room's walls), so the plan is
        /// shown square to the walls, then fits it to <paramref name="pontos"/> + margin in the
        /// turned frame. False if Revit refused (the plan stays square to the project).
        /// </summary>
        public static bool GirarPlanta(Document doc, ViewPlan v, double ang, IList<XYZ> pontos, double margem, bool mostrar)
        {
            if (Math.Abs(ang) < 1e-6 || pontos.Count == 0) return true;
            try
            {
                v.CropBoxActive = true;
                ElementId crop = ElementoDoRecorte(doc, v);
                if (crop == ElementId.InvalidElementId) return false;
                BoundingBoxXYZ cb0 = v.CropBox;
                XYZ c = cb0.Transform.OfPoint((cb0.Min + cb0.Max) / 2);
                ElementTransformUtils.RotateElement(doc, crop, Line.CreateBound(new XYZ(c.X, c.Y, 0), new XYZ(c.X, c.Y, 1)), ang);
                doc.Regenerate();
                BoundingBoxXYZ cb = v.CropBox;
                Transform inv = cb.Transform.Inverse;
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                foreach (XYZ p in pontos)
                {
                    XYZ l = inv.OfPoint(new XYZ(p.X, p.Y, cb.Transform.Origin.Z));
                    minX = Math.Min(minX, l.X); maxX = Math.Max(maxX, l.X);
                    minY = Math.Min(minY, l.Y); maxY = Math.Max(maxY, l.Y);
                }
                v.CropBox = new BoundingBoxXYZ
                {
                    Transform = cb.Transform,
                    Min = new XYZ(minX - margem, minY - margem, cb.Min.Z),
                    Max = new XYZ(maxX + margem, maxY + margem, cb.Max.Z)
                };
                try { v.CropBoxVisible = mostrar; } catch { }
                return true;
            }
            catch (Exception ex)
            {
                Logger.Info("[JA Tools] Planta girada: " + ex.Message);
                return false;
            }
        }

        /// <summary>Every point of the room outline (plan).</summary>
        public static List<XYZ> Contorno(Room r)
        {
            var pts = new List<XYZ>();
            foreach (var loop in RevitUtil.Boundary(r))
                foreach (BoundarySegment s in loop)
                    pts.AddRange(s.GetCurve().Tessellate());
            return pts;
        }
    }
}
