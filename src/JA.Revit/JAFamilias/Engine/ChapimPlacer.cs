using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace JAEsquadrias.Engine
{
    /// <summary>
    /// Puts a chapim (line-based Generic Model) on top of walls: one instance per wall, on the
    /// wall's centre line (whatever its location line is), at the top of the wall, with the
    /// family's +Y (exterior overhang) on the wall's exterior side.
    /// </summary>
    /// <summary>A curved wall seen by the chapim: centre, radius of the wall centre line, which side is the exterior, start and sweep (CCW, radians).</summary>
    public sealed class ArcoParede
    {
        public XYZ Centro;
        public double RaioMm;
        public bool ExteriorParaFora;
        public double Inicio, Varredura;
        public double ComprimentoMm => RaioMm * Varredura;
        /// <summary>Family key: radius rounded to the mm and the side.</summary>
        public string Chave => Math.Round(RaioMm).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + (ExteriorParaFora ? "" : "i");
    }

    public static class ChapimPlacer
    {
        private const double Mm = 1.0 / 304.8;

        /// <summary>
        /// Arc of a curved wall at its centre line (whatever the location line is). The exterior side
        /// comes from Wall.Orientation against the radius at the middle of the arc (else from the
        /// drawing direction and Flipped). Null for straight walls.
        /// </summary>
        public static ArcoParede Arco(Wall wall)
        {
            if (!(wall.Location is LocationCurve lc) || !(lc.Curve is Arc arc)) return null;
            XYZ c = arc.Center;
            XYZ mid = arc.Evaluate(0.5, true);
            XYZ radial = new XYZ(mid.X - c.X, mid.Y - c.Y, 0);
            if (radial.GetLength() < 1e-9) return null;
            radial = radial.Normalize();
            double sinal = 0;
            try
            {
                XYZ o = wall.Orientation;
                double d = new XYZ(o.X, o.Y, 0).DotProduct(radial);
                if (Math.Abs(d) > 0.3) sinal = Math.Sign(d);
            }
            catch { }
            if (sinal == 0)
            {
                XYZ t = arc.ComputeDerivatives(0.5, true).BasisX;
                XYZ left = XYZ.BasisZ.CrossProduct(new XYZ(t.X, t.Y, 0)).Normalize();
                if (wall.Flipped) left = left.Negate();
                sinal = left.DotProduct(radial) >= 0 ? 1 : -1;
            }
            double rc = arc.Radius + sinal * CentreShift(wall);      // centre line radius (feet)

            double A(XYZ p) => Math.Atan2(p.Y - c.Y, p.X - c.X);
            double Norm(double a) { a %= 2 * Math.PI; return a < 0 ? a + 2 * Math.PI : a; }
            double a0 = A(arc.GetEndPoint(0)), a1 = A(arc.GetEndPoint(1)), am = A(mid);
            double ini, varr;
            if (Norm(am - a0) < Norm(a1 - a0)) { ini = a0; varr = Norm(a1 - a0); }     // counter-clockwise from end 0
            else { ini = a1; varr = Norm(a0 - a1); }
            if (varr < 1e-6) varr = 2 * Math.PI;
            return new ArcoParede { Centro = new XYZ(c.X, c.Y, 0), RaioMm = rc * 304.8, ExteriorParaFora = sinal > 0, Inicio = ini, Varredura = varr };
        }

        /// <summary>
        /// Curved chapim (free family, revolution about its origin from local +X counter-clockwise):
        /// placed at the arc centre, turned to the start of the arc, "Ângulo do arco" = sweep,
        /// raised to the top of the wall.
        /// </summary>
        public static FamilyInstance PlaceArc(Document doc, FamilySymbol symbol, Wall wall, ArcoParede arco, bool anguloParametrico, List<string> warnings)
        {
            Level level = doc.GetElement(wall.LevelId) as Level;
            if (level == null) { warnings.Add($"Parede {wall.Id.Value} sem nível base."); return null; }
            double top = TopOf(wall);
            XYZ at = new XYZ(arco.Centro.X, arco.Centro.Y, level.ProjectElevation);
            FamilyInstance fi = doc.Create.NewFamilyInstance(at, symbol, level, StructuralType.NonStructural);
            if (fi == null) return null;
            if (Math.Abs(arco.Inicio) > 1e-9)
                ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(at, at + XYZ.BasisZ), arco.Inicio);
            if (anguloParametrico)
            {
                Parameter ang = fi.LookupParameter(PecaGenerator.ParamAngulo);
                if (ang == null || ang.IsReadOnly || !ang.Set(arco.Varredura))
                    warnings.Add($"Parede {wall.Id.Value}: o ângulo do chapim curvo não foi aplicado (confira “{PecaGenerator.ParamAngulo}”).");
            }
            // metres of chapim when the family could not compute them itself
            try
            {
                Parameter comp = fi.get_Parameter(SharedParams.Comprimento.Guid);
                if (comp != null && !comp.IsReadOnly && comp.StorageType == StorageType.Double) comp.Set(arco.ComprimentoMm * Mm);
            }
            catch { }
            doc.Regenerate();

            double offset = top - level.ProjectElevation;
            bool set = false;
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_OFFSET_POS_PARAM })
            {
                Parameter p = fi.get_Parameter(bip);
                if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) continue;
                try { p.Set(offset); set = true; break; } catch { }
            }
            doc.Regenerate();
            double now = CurrentZ(fi);
            if (!double.IsNaN(now) && Math.Abs(now - top) > 0.5 * Mm)
            {
                try { ElementTransformUtils.MoveElement(doc, fi.Id, new XYZ(0, 0, top - now)); set = true; }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Chapim curvo não elevado: " + ex.Message); }
            }
            if (!set) warnings.Add($"Parede {wall.Id.Value}: confira a altura do chapim (deslocamento não aplicado).");
            return fi;
        }

        /// <summary>Wall thickness rounded to the mm.</summary>
        public static double ThicknessMm(Wall w) => Math.Round(w.Width * 304.8);

        public static int Apply(Document doc, FamilySymbol symbol, IEnumerable<Wall> walls, List<string> warnings)
        {
            int placed = 0;
            foreach (Wall wall in walls)
            {
                try
                {
                    if (Place(doc, symbol, wall, warnings) != null) placed++;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Parede {wall.Id.Value}: chapim não aplicado ({ex.Message}).");
                    Logger.Warn("[JA Famílias] Chapim na parede " + wall.Id.Value + ": " + ex);
                }
            }
            return placed;
        }

        private static FamilyInstance Place(Document doc, FamilySymbol symbol, Wall wall, List<string> warnings)
        {
            if (!(wall.Location is LocationCurve lc) || lc.Curve == null) { warnings.Add($"Parede {wall.Id.Value} sem linha de locação."); return null; }
            Curve curve = lc.Curve;
            if (!(curve is Line))
            {
                // arcs get the curved family (ApplyChapim); other curves (ellipses, splines) stay on the chord
                warnings.Add($"Parede {wall.Id.Value} tem curva que não é arco de círculo: o chapim foi aplicado na corda (reta entre as pontas).");
            }
            XYZ a = curve.GetEndPoint(0), b = curve.GetEndPoint(1);
            if (a.DistanceTo(b) < 50 * Mm) { warnings.Add($"Parede {wall.Id.Value} curta demais."); return null; }

            // centre line of the wall
            XYZ ext = wall.Orientation;                 // exterior side
            double shift = CentreShift(wall);           // feet, along ext
            a += ext * shift;
            b += ext * shift;

            // family +Y = left of the line: make it the exterior side
            XYZ dir = (b - a).Normalize();
            XYZ left = XYZ.BasisZ.CrossProduct(dir);
            if (left.DotProduct(ext) < 0) { XYZ t = a; a = b; b = t; }

            Level level = doc.GetElement(wall.LevelId) as Level;
            if (level == null) { warnings.Add($"Parede {wall.Id.Value} sem nível base."); return null; }
            double top = TopOf(wall);
            double z = level.ProjectElevation;
            Line line = Line.CreateBound(new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z));

            FamilyInstance fi = doc.Create.NewFamilyInstance(line, symbol, level, StructuralType.NonStructural);
            if (fi == null) return null;
            doc.Regenerate();

            double offset = top - level.ProjectElevation;
            bool set = false;
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_OFFSET_POS_PARAM })
            {
                Parameter p = fi.get_Parameter(bip);
                if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) continue;
                try { p.Set(offset); set = true; break; } catch { }
            }
            doc.Regenerate();
            // whatever the template did, make sure the chapim ends up on top of the wall
            double now = CurrentZ(fi);
            if (!double.IsNaN(now) && Math.Abs(now - top) > 0.5 * Mm)
            {
                try { ElementTransformUtils.MoveElement(doc, fi.Id, new XYZ(0, 0, top - now)); set = true; }
                catch (Exception ex) { Logger.Warn("[JA Famílias] Chapim não elevado: " + ex.Message); }
            }
            if (!set) warnings.Add($"Parede {wall.Id.Value}: confira a altura do chapim (deslocamento não aplicado).");
            return fi;
        }

        /// <summary>Distance (feet, along Orientation) from the location line to the wall centre.</summary>
        private static double CentreShift(Wall wall)
        {
            double W = wall.Width;
            int key = 0;
            try { key = wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsInteger() ?? 0; } catch { }
            // distance of the location line from the exterior face
            double dl;
            switch (key)
            {
                case 2: dl = 0; break;            // finish face: exterior
                case 3: dl = W; break;            // finish face: interior
                case 1: case 4: case 5:           // core based
                    dl = CoreDistance(wall, key);
                    if (double.IsNaN(dl)) dl = W / 2;
                    break;
                default: dl = W / 2; break;       // wall centreline
            }
            // P(d) = centre + ext·(W/2 − d)  ⇒  centre = P(dl) + ext·(dl − W/2)
            return dl - W / 2;
        }

        private static double CoreDistance(Wall wall, int key)
        {
            try
            {
                CompoundStructure cs = wall.WallType?.GetCompoundStructure();
                if (cs == null) return double.NaN;
                int first = cs.GetFirstCoreLayerIndex(), last = cs.GetLastCoreLayerIndex();
                double before = 0, through = 0;
                for (int i = 0; i < cs.LayerCount; i++)
                {
                    double w = cs.GetLayerWidth(i);
                    if (i < first) before += w;
                    if (i <= last) through += w;
                }
                if (key == 4) return before;              // core face exterior
                if (key == 5) return through;             // core face interior
                return (before + through) / 2;            // core centreline
            }
            catch { return double.NaN; }
        }

        private static double TopOf(Wall wall)
        {
            BoundingBoxXYZ bb = wall.get_BoundingBox(null);
            if (bb != null) return bb.Max.Z;
            Level level = wall.Document.GetElement(wall.LevelId) as Level;
            double baseOff = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET)?.AsDouble() ?? 0;
            double h = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM)?.AsDouble() ?? 0;
            return (level?.ProjectElevation ?? 0) + baseOff + h;
        }

        /// <summary>
        /// A line-based JA piece (rodapé, guarda-corpo) along a → b with its bottom at zBase
        /// (absolute, feet); the piece stands on the left of a → b (family +Y). Inside a transaction.
        /// </summary>
        public static FamilyInstance PlaceLine(Document doc, FamilySymbol symbol, XYZ a, XYZ b, Level level, double zBase, List<string> warnings)
        {
            if (!symbol.IsActive) symbol.Activate();
            double z = level.ProjectElevation;
            if (new XYZ(a.X, a.Y, 0).DistanceTo(new XYZ(b.X, b.Y, 0)) < 20 * Mm) return null;
            Line line = Line.CreateBound(new XYZ(a.X, a.Y, z), new XYZ(b.X, b.Y, z));
            FamilyInstance fi = doc.Create.NewFamilyInstance(line, symbol, level, StructuralType.NonStructural);
            if (fi == null) return null;
            doc.Regenerate();
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_OFFSET_POS_PARAM })
            {
                Parameter p = fi.get_Parameter(bip);
                if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) continue;
                try { p.Set(zBase - level.ProjectElevation); break; } catch { }
            }
            doc.Regenerate();
            double now = CurrentZ(fi);
            if (!double.IsNaN(now) && Math.Abs(now - zBase) > 0.5 * Mm)
            {
                try { ElementTransformUtils.MoveElement(doc, fi.Id, new XYZ(0, 0, zBase - now)); }
                catch (Exception ex) { warnings?.Add("Altura não acertada: " + ex.Message); }
            }
            return fi;
        }

        private static double CurrentZ(FamilyInstance fi)
        {
            try
            {
                // the profile starts at local Z = 0 (bottom of the stone = top of the wall)
                BoundingBoxXYZ bb = fi.get_BoundingBox(null);
                if (bb != null) return bb.Min.Z;
            }
            catch { }
            return double.NaN;
        }
    }
}
