using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using JAEsquadrias.Core;

namespace JAEsquadrias.Engine
{
    /// <summary>
    /// Makes a window/door family follow ANY host wall (thickness) and the two finishes, which are
    /// INSTANCE parameters ("Revestimento externo" / "Revestimento interno"):
    ///  - two reference planes locked to the faces of the template wall (they move with the real
    ///    wall in the project) and a reporting dimension "Espessura da parede" between them;
    ///  - one "anchor" reference plane per kind of position (<see cref="Forma"/>): it stands
    ///    <see cref="K"/> mm away from the face it follows (so its dimension is never zero), plus
    ///    the finishes through a labelled dimension (formula), or a locked one when no finish counts;
    ///  - every solid is sketched on the anchor of one of its depths, with constant start/end
    ///    offsets; a depth that follows another face (a door frame across the wall, the sill to
    ///    the inner face) gets its offset from an "JA aux" instance parameter with a formula.
    /// Plan lines are handled by <see cref="PlantaAninhada"/> (nested detail items placed on the
    /// anchors) and by alignments for the lines that cross the wall.
    /// </summary>
    internal sealed class AncorasParede
    {
        private const double Mm = 1.0 / 304.8;
        /// <summary>Distance (mm) between an anchor plane and the face it follows.</summary>
        public const double K = 1000;
        public const string NomeEspessura = "Espessura da parede";
        public const string NomeRevExt = "Revestimento externo";
        public const string NomeRevInt = "Revestimento interno";

        public sealed class Ancora
        {
            public ReferencePlane Plano;
            /// <summary>Position of the plane itself as a function of t, rx, ri.</summary>
            public Forma Forma;
            /// <summary>+1 = stands outside (exterior side) of what it follows, -1 = inside.</summary>
            public double Sinal;
            public SketchPlane Esboco;
        }

        private readonly Document _fam;
        private readonly FamilyManager _fm;
        private readonly ViewPlan _plan;
        private readonly XYZ _u, _n;
        private readonly double _center, _z, _xa;
        private readonly Action<string> _warn;
        public double T, Rx, Ri;
        public FamilyParameter PEsp, PRx, PRi;

        private ReferencePlane _ext, _int, _mid;
        private readonly Dictionary<string, Ancora> _anc = new Dictionary<string, Ancora>();
        private readonly Dictionary<string, ReferencePlane> _depth = new Dictionary<string, ReferencePlane>();
        private readonly Dictionary<string, FamilyParameter> _aux = new Dictionary<string, FamilyParameter>();
        private int _auxN, _planeN, _dimN;
        public int Falhas;

        /// <param name="u">Along the wall (right, seen from outside).</param>
        /// <param name="n">Across the wall, towards the exterior.</param>
        /// <param name="xa">Where (feet along u) the helper planes and dimensions are drawn.</param>
        public AncorasParede(Document fam, ViewPlan plan, XYZ u, XYZ n, double extFeet, double intFeet, double xa, double rx, double ri, Action<string> warn)
        {
            _fam = fam;
            _fm = fam.FamilyManager;
            _plan = plan;
            _u = u;
            _n = n;
            _center = (extFeet + intFeet) / 2;
            T = (extFeet - intFeet) / Mm;
            Rx = rx;
            Ri = ri;
            _xa = xa;
            _z = plan.GenLevel?.ProjectElevation ?? 0;
            _warn = warn;
        }

        /// <summary>Point of the plan (x = feet along u, pos = mm across the wall from its centre).</summary>
        public XYZ Pt(double xFeet, double posMm) => _u * xFeet + _n * (_center + posMm * Mm) + XYZ.BasisZ * _z;

        /// <summary>Planes on the wall faces, the reporting thickness and the two finishes. False = not adaptive.</summary>
        public bool Iniciar(Reference faceExt, Reference faceInt)
        {
            if (faceExt == null || faceInt == null || T < 10) return false;
            _ext = Plano("JA - face externa da parede", T / 2);
            _int = Plano("JA - face interna da parede", -T / 2);
            if (_ext == null || _int == null) return false;
            try
            {
                _fam.FamilyCreate.NewAlignment(_plan, faceExt, _ext.GetReference());
                _fam.FamilyCreate.NewAlignment(_plan, faceInt, _int.GetReference());
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA Famílias] Planos não travados na parede do modelo: " + ex.Message);
                return false;
            }

            PRx = Comprimento(NomeRevExt, Rx, GroupTypeId.Geometry);
            PRi = Comprimento(NomeRevInt, Ri, GroupTypeId.Geometry);
            if (PRx == null || PRi == null) return false;

            // reporting thickness of the host wall (useful in schedules too)
            try
            {
                Dimension d = Cota(_ext, _int);
                PEsp = _fm.get_Parameter(NomeEspessura) ?? _fm.AddParameter(NomeEspessura, GroupTypeId.Geometry, SpecTypeId.Length, true);
                d.FamilyLabel = PEsp;
                try { _fm.MakeReporting(PEsp); } catch (Exception ex) { Logger.Warn("[JA Famílias] Espessura não virou parâmetro de relatório: " + ex.Message); }
                if (!PEsp.IsReporting) { PEsp = null; Logger.Warn("[JA Famílias] Parâmetro de espessura da parede não é de relatório."); }
            }
            catch (Exception ex)
            {
                PEsp = null;
                Logger.Warn("[JA Famílias] Cota da espessura da parede: " + ex.Message);
            }
            return true;
        }

        private FamilyParameter Comprimento(string name, double mm, ForgeTypeId group)
        {
            try
            {
                FamilyParameter p = _fm.get_Parameter(name) ?? _fm.AddParameter(name, group, SpecTypeId.Length, true);
                if (!p.IsInstance) { _warn?.Invoke($"O parâmetro '{name}' já existe como parâmetro de tipo no modelo de família."); return null; }
                _fm.Set(p, mm * Mm);
                return p;
            }
            catch (Exception ex)
            {
                _warn?.Invoke($"Parâmetro '{name}' não criado: {ex.Message}");
                return null;
            }
        }

        // ------------------------------------------------------------------ planes and dimensions
        private ReferencePlane Plano(string name, double posMm)
        {
            try
            {
                XYZ a = Pt(_xa, posMm), b = Pt(_xa + 1.0, posMm);
                ReferencePlane rp = _fam.FamilyCreate.NewReferencePlane(a, b, XYZ.BasisZ, _plan);
                try { rp.Name = name + (++_planeN > 2 ? " " + _planeN : ""); } catch { }
                return rp;
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA Famílias] Plano de referência " + name + ": " + ex.Message);
                return null;
            }
        }

        private Dimension Cota(params ReferencePlane[] planes)
        {
            var ra = new ReferenceArray();
            foreach (var p in planes) ra.Append(p.GetReference());
            double x = _xa - (0.4 + 0.12 * (_dimN++ % 12));   // rows of dimensions left of the opening
            double lo = -T / 2 - 2 * K, hi = T / 2 + 2 * K;
            Line line = Line.CreateBound(Pt(x, lo), Pt(x, hi));
            return _fam.FamilyCreate.NewDimension(_plan, line, ra);
        }

        private ReferencePlane Meio()
        {
            if (_mid != null) return _mid;
            _mid = Plano("JA - centro da parede", 0);
            if (_mid == null) return null;
            try
            {
                Dimension eq = Cota(_ext, _mid, _int);
                eq.AreSegmentsEqual = true;
            }
            catch (Exception ex) { Logger.Warn("[JA Famílias] Centro da parede (EQ): " + ex.Message); Falhas++; }
            return _mid;
        }

        /// <summary>The anchor plane that moves like <paramref name="f"/>; null when it can't be built.</summary>
        public Ancora AncoraDe(Forma f)
        {
            if (f.Fixa) return null;
            if (_anc.TryGetValue(f.Chave, out Ancora a)) return a;
            ReferencePlane bas = Forma.Near(f.At, 0.5) ? _ext : Forma.Near(f.At, -0.5) ? _int : Forma.Near(f.At, 0) ? Meio() : null;
            if (bas == null) return null;
            double s = f.At < -1e-6 ? -1 : 1;
            var forma = new Forma { C = s * K, At = f.At, Ax = f.Ax, Ai = f.Ai };
            ReferencePlane rp = Plano("JA - âncora", forma.Valor(T, Rx, Ri));
            if (rp == null) return null;
            try
            {
                Dimension d = Cota(bas, rp);
                if (Forma.Near(f.Ax, 0) && Forma.Near(f.Ai, 0)) d.IsLocked = true;
                else
                {
                    FamilyParameter p = Aux("1000 mm" + Termo(s * f.Ax, NomeRevExt) + Termo(s * f.Ai, NomeRevInt));
                    if (p == null) return null;
                    d.FamilyLabel = p;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA Famílias] Âncora " + f.Chave + ": " + ex.Message);
                Falhas++;
                return null;
            }
            a = new Ancora { Plano = rp, Forma = forma, Sinal = s };
            _anc[f.Chave] = a;
            return a;
        }

        /// <summary>A reference plane exactly at <paramref name="f"/>, locked to its anchor.</summary>
        public ReferencePlane PlanoEm(Forma f)
        {
            if (_depth.TryGetValue(f.ChaveCompleta, out ReferencePlane rp)) return rp;
            Ancora a = AncoraDe(f);
            if (a == null) return null;
            rp = Plano("JA - linha", f.Valor(T, Rx, Ri));
            if (rp == null) return null;
            try { Cota(a.Plano, rp).IsLocked = true; }
            catch (Exception ex) { Logger.Warn("[JA Famílias] Plano da planta: " + ex.Message); Falhas++; return null; }
            _depth[f.ChaveCompleta] = rp;
            return rp;
        }

        public SketchPlane EsbocoEm(ReferencePlane rp)
        {
            try { return SketchPlane.Create(_fam, rp.Id); }
            catch { return SketchPlane.Create(_fam, rp.GetReference()); }
        }

        // ------------------------------------------------------------------ formulas
        /// <summary>Instance length parameter driven by a formula (shared by every use of the same formula).</summary>
        public FamilyParameter Aux(string formula)
        {
            if (_aux.TryGetValue(formula, out FamilyParameter p)) return p;
            try
            {
                p = _fm.AddParameter("JA aux " + (++_auxN), GroupTypeId.Constraints, SpecTypeId.Length, true);
                _fm.SetFormula(p, formula);
                _aux[formula] = p;
                return p;
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA Famílias] Fórmula '" + formula + "': " + ex.Message);
                Falhas++;
                return null;
            }
        }

        /// <summary>Formula of a length = form (mm), written with the instance parameters.</summary>
        public string Expressao(Forma f, double sinal)
        {
            if (PEsp == null && !Forma.Near(f.At, 0)) return null;
            string s = "0 mm" + Constante(sinal * f.C);
            s += Termo(sinal * f.At, NomeEspessura) + Termo(sinal * f.Ax, NomeRevExt) + Termo(sinal * f.Ai, NomeRevInt);
            return s;
        }

        private static string Constante(double mm)
        {
            long k = (long)Math.Round(mm * 100);
            if (k == 0) return "";
            string sign = k < 0 ? " - " : " + ";
            k = Math.Abs(k);
            return sign + (k % 100 == 0 ? (k / 100).ToString(CultureInfo.InvariantCulture) + " mm" : "(" + k.ToString(CultureInfo.InvariantCulture) + " mm / 100)");
        }

        private static string Termo(double coef, string name)
        {
            long h = (long)Math.Round(coef * 2);    // half units
            if (h == 0) return "";
            string sign = h < 0 ? " - " : " + ";
            h = Math.Abs(h);
            if (h == 2) return sign + name;
            if (h == 1) return sign + name + " / 2";
            return sign + name + (h % 2 == 0 ? " * " + (h / 2).ToString(CultureInfo.InvariantCulture) : " * " + h.ToString(CultureInfo.InvariantCulture) + " / 2");
        }

        // ------------------------------------------------------------------ solids
        /// <summary>
        /// Extrusion of a profile drawn in the elevation (x, y in mm, as the Layout gives them)
        /// between the positions f0 and f1 across the wall, sketched on an anchor plane.
        /// Null when neither depth can be anchored (the caller draws it the old way).
        /// </summary>
        public Extrusion Extrusao(bool solid, List<List<P2>> loops, Func<double, double, double, XYZ> ponto, Forma f0, Forma f1)
        {
            Forma fa = !f0.Fixa ? f0 : f1;
            Ancora a = AncoraDe(fa);
            if (a == null) return null;
            if (a.Esboco == null || !a.Esboco.IsValidObject) a.Esboco = EsbocoEm(a.Plano);
            double posA = a.Forma.Valor(T, Rx, Ri);
            double nn = a.Esboco.GetPlane().Normal.DotProduct(_n) >= 0 ? 1 : -1;

            var arr = new CurveArrArray();
            foreach (var loop in loops)
            {
                if (loop.Count < 3) continue;
                var ca = new CurveArray();
                for (int i = 0; i < loop.Count; i++)
                {
                    P2 p = loop[i], q = loop[(i + 1) % loop.Count];
                    ca.Append(Line.CreateBound(ponto(p.X, p.Y, posA), ponto(q.X, q.Y, posA)));
                }
                arr.Append(ca);
            }
            if (arr.Size == 0) return null;

            double v0 = (f0.Valor(T, Rx, Ri) - posA) * nn, v1 = (f1.Valor(T, Rx, Ri) - posA) * nn;
            Forma fs = f0, fe = f1;
            if (v0 > v1) { double tmp = v0; v0 = v1; v1 = tmp; fs = f1; fe = f0; }
            if (v1 - v0 < 0.5) return null;

            Extrusion ex = _fam.FamilyCreate.NewExtrusion(solid, arr, a.Esboco, 1.0);
            if (v0 >= 0) { ex.EndOffset = v1 * Mm; ex.StartOffset = v0 * Mm; }
            else { ex.StartOffset = v0 * Mm; ex.EndOffset = v1 * Mm; }

            Vincular(ex, BuiltInParameter.EXTRUSION_START_PARAM, fs, a, nn);
            Vincular(ex, BuiltInParameter.EXTRUSION_END_PARAM, fe, a, nn);
            return ex;
        }

        /// <summary>An extrusion end that does not move with the anchor follows its own formula.</summary>
        private void Vincular(Extrusion ex, BuiltInParameter bip, Forma f, Ancora a, double nn)
        {
            if (f.Fixa || f.MesmaAncora(a.Forma)) return;
            var rel = new Forma { C = f.C - a.Forma.C, At = f.At - a.Forma.At, Ax = f.Ax - a.Forma.Ax, Ai = f.Ai - a.Forma.Ai };
            string formula = Expressao(rel, nn);
            if (formula == null) { Falhas++; return; }
            FamilyParameter p = Aux(formula);
            if (p == null) return;
            try
            {
                Parameter ep = ex.get_Parameter(bip);
                if (ep != null) _fm.AssociateElementParameterToFamilyParameter(ep, p);
            }
            catch (Exception e) { Logger.Warn("[JA Famílias] Profundidade variável não associada: " + e.Message); Falhas++; }
        }

        // ------------------------------------------------------------------ plan lines across the wall
        /// <summary>
        /// A plan line across the wall (from one face-dependent depth to another): an invisible
        /// helper line on each end's plane holds the end point ("line and point" alignment).
        /// </summary>
        public bool PrenderPontas(SymbolicCurve linha, Forma fa, Forma fb, SketchPlane planta, double xFeet)
        {
            bool ok = true;
            for (int end = 0; end < 2; end++)
            {
                Forma f = end == 0 ? fa : fb;
                if (f.Fixa) { ok = false; continue; }
                try
                {
                    ReferencePlane rp = PlanoEm(f);
                    if (rp == null) { ok = false; continue; }
                    double pos = f.Valor(T, Rx, Ri);
                    double z = linha.GeometryCurve.GetEndPoint(0).Z;     // on the plan sketch plane of the line
                    XYZ h0 = Pt(xFeet - 0.05, pos), h1 = Pt(xFeet + 0.05, pos);
                    var helper = _fam.FamilyCreate.NewSymbolicCurve(Line.CreateBound(new XYZ(h0.X, h0.Y, z), new XYZ(h1.X, h1.Y, z)), planta);
                    Parameter vis = helper.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                    if (vis != null && !vis.IsReadOnly) vis.Set(0);
                    _fam.FamilyCreate.NewAlignment(_plan, rp.GetReference(), helper.GeometryCurve.Reference);
                    _fam.FamilyCreate.NewAlignment(_plan, helper.GeometryCurve.Reference, linha.GeometryCurve.GetEndPointReference(end));
                }
                catch (Exception ex)
                {
                    Logger.Warn("[JA Famílias] Ponta de linha de planta não presa: " + ex.Message);
                    ok = false;
                }
            }
            if (!ok) Falhas++;
            return ok;
        }

        /// <summary>Locks a nested plan group (detail item) to its anchor: constant distance K.</summary>
        public bool PrenderGrupo(FamilyInstance inst, Ancora a)
        {
            try
            {
                var type = Math.Abs(_n.Y) >= Math.Abs(_n.X) ? FamilyInstanceReferenceType.CenterFrontBack : FamilyInstanceReferenceType.CenterLeftRight;
                Reference r = inst.GetReferences(type).FirstOrDefault();
                if (r == null) return false;
                var ra = new ReferenceArray();
                ra.Append(a.Plano.GetReference());
                ra.Append(r);
                double x = _xa - (0.4 + 0.12 * (_dimN++ % 12));
                Line line = Line.CreateBound(Pt(x, -T / 2 - 2 * K), Pt(x, T / 2 + 2 * K));
                _fam.FamilyCreate.NewDimension(_plan, line, ra).IsLocked = true;
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("[JA Famílias] Grupo da planta não preso à âncora: " + ex.Message);
                Falhas++;
                return false;
            }
        }
    }
}
