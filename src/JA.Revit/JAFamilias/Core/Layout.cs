using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JAEsquadrias.Core
{
    /// <summary>2D point in the elevation (mm): X to the right as seen from the exterior, Y up,
    /// origin at the bottom-left corner of the opening.</summary>
    public struct P2
    {
        public double X, Y;
        public P2(double x, double y) { X = x; Y = y; }
    }

    /// <summary>Axis-aligned rectangle in the elevation (mm).</summary>
    public struct Rc
    {
        public double X0, Y0, X1, Y1;
        public Rc(double x0, double y0, double x1, double y1) { X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; }
        public double W => X1 - X0;
        public double H => Y1 - Y0;
        public double Xm => (X0 + X1) / 2;
        public double Ym => (Y0 + Y1) / 2;
        public double Area => Math.Max(0, W) * Math.Max(0, H);
        public bool Valid => W > 1 && H > 1;
        public Rc Inset(double d) => new Rc(X0 + d, Y0 + d, X1 - d, Y1 - d);
        public bool Contains(double x, double y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
        public List<P2> Loop() => new List<P2> { new P2(X0, Y0), new P2(X1, Y0), new P2(X1, Y1), new P2(X0, Y1) };
    }

    public enum Papel { Marco, Travessa, Folha, Vidro, Veneziana, Painel, Puxador, Peitoril, Soleira, Guarnicao, Batente }
    public enum MaterialSlot { Perfil, Vidro, Painel, Peitoril }

    /// <summary>
    /// A solid of the esquadria: a profile drawn in the elevation plane (one or more loops, the
    /// first is the outline, the others are holes) extruded across the wall between depths D0 and
    /// D1. Depth (mm) is measured from the EXTERIOR face of the frame towards the interior.
    /// </summary>
    public sealed class Prisma
    {
        public List<List<P2>> Loops = new List<List<P2>>();
        public double D0, D1;
        public Papel Papel;
        public MaterialSlot Material;
        public bool Coarse = true, Medium = true, Fine = true;

        public static Prisma Box(Rc r, double d0, double d1, Papel papel, MaterialSlot mat)
        {
            var p = new Prisma { D0 = Math.Min(d0, d1), D1 = Math.Max(d0, d1), Papel = papel, Material = mat };
            p.Loops.Add(r.Loop());
            return p;
        }

        public static Prisma Ring(Rc outer, Rc inner, double d0, double d1, Papel papel, MaterialSlot mat)
        {
            var p = Box(outer, d0, d1, papel, mat);
            if (inner.Valid) p.Loops.Add(inner.Loop());
            return p;
        }

        public Prisma Lod(bool coarse, bool medium, bool fine) { Coarse = coarse; Medium = medium; Fine = fine; return this; }
    }

    /// <summary>Opening symbol line in the elevation (dashed = opens away from the viewer).</summary>
    public sealed class Traco
    {
        public P2 A, B;
        public bool Tracejado;
        public Traco(P2 a, P2 b, bool dashed) { A = a; B = b; Tracejado = dashed; }
    }

    /// <summary>
    /// Plan line (2D, like a CAD block): a segment, or an arc through Mid when IsArc. Points are
    /// (X along the wall as seen from outside, Y = depth from the frame's exterior face towards
    /// the interior). Fino = thin line (swing arcs, glass).
    /// </summary>
    public sealed class TracoPlanta
    {
        public P2 A, B, Mid;
        public bool IsArc;
        public bool Fino;
        public bool Tracejado;
        /// <summary>What the line belongs to: each group can be switched on/off in Revit.</summary>
        public GrupoPlanta Grupo;
        /// <summary>Default visibility (the line is always generated so it can be turned on in Revit).</summary>
        public bool Visivel = true;
    }

    /// <summary>Groups of the 2D plan; each becomes a Yes/No instance parameter of the family.</summary>
    public enum GrupoPlanta { Marco, Folha, Arco, FolhaFechada, Guarnicao, Pedra, Verga, Setas, Vao }

    /// <summary>One module of the grid: its outside rectangle and its clear opening (luz).</summary>
    public sealed class Modulo
    {
        public int Row, Col;
        public Rc Mod, Luz;
        public Celula Celula;
    }

    /// <summary>
    /// Turns an <see cref="EsquadriaConfig"/> into geometry: the solids that become Revit
    /// extrusions, the elevation opening symbols, the 2D plan representation (marco, folhas,
    /// folha de porta aberta com arco, peitoril/soleira, guarnições) and the numbers (thermal
    /// transmittance, light and ventilation areas). The same result feeds the preview, the
    /// family generator and the report, so what is drawn on screen is exactly what is modelled.
    /// </summary>
    public sealed class Layout
    {
        public readonly List<Prisma> Prismas = new List<Prisma>();
        public readonly List<Traco> Simbolos = new List<Traco>();
        public readonly List<TracoPlanta> Planta = new List<TracoPlanta>();
        public readonly List<Modulo> Modulos = new List<Modulo>();
        public readonly List<string> Avisos = new List<string>();

        public double W, H;
        /// <summary>Areas in m².</summary>
        public double AreaTotal, AreaVidro, AreaIluminacao, AreaVentilacao, AreaOpaca;
        /// <summary>Window thermal transmittance Uw (W/m²K), EN ISO 10077-1 simplified.</summary>
        public double Uw;
        public int NumFolhas;
        /// <summary>Faces of the Revit wall in the depth system (mm): exterior and interior.</summary>
        public double ParedeExt, ParedeInt;
        /// <summary>Finished faces (wall + revestimento) in the depth system (mm).</summary>
        public double FaceExt, FaceInt;
        /// <summary>Stone piece (peitoril / soleira) in plan: x and depth range, mm; null when off.</summary>
        public Rc? Pedra;
        /// <summary>Frame depth actually used (doors with the frame across the wall use the wall thickness).</summary>
        public double MarcoProf => _mp;

        private EsquadriaConfig _c;
        private VidroInfo _vidro;
        private double _mf, _mp, _ff, _fp, _tv, _bottom;
        private double _glassPerimeter, _louverArea, _panelArea;
        private int _cutRow = -1;
        // current plan group (set before drawing a set of plan lines)
        private GrupoPlanta _grp = GrupoPlanta.Marco;
        private bool _grpVis = true, _grpDash;

        private void G(GrupoPlanta g, bool visible, bool dashed = false) { _grp = g; _grpVis = visible; _grpDash = dashed; }

        /// <summary>
        /// Offset (mm) of the frame's exterior face from the wall centre, towards the exterior.
        /// Shared by the family generator so the plan symbols and the solids line up.
        /// </summary>
        public static double FrameOffsetFromCenter(EsquadriaConfig c)
        {
            // curtain wall: no wall around it, the frame is centred on the curtain wall's plane
            if (c.ParedeCortina) return c.MarcoProfMm / 2 + c.RecuoMm;
            // positions refer to the FINISHED faces: Revit wall + revestimento on each side
            double e = c.ParedeMm, rx = c.RevestimentoExtMm, ri = c.RevestimentoIntMm;
            double faceExt = e / 2 + rx, faceInt = -(e / 2 + ri);       // offsets from the wall centre (towards the exterior)
            double mp = c.MarcoNaParede ? e + rx + ri : c.MarcoProfMm, r = c.RecuoMm;
            if (c.MarcoNaParede) return faceExt;
            switch (c.Posicao)
            {
                case PosicaoParede.Externa: return faceExt - r;
                case PosicaoParede.Interna: return faceInt + mp + r;
                default: return (faceExt + faceInt) / 2 + mp / 2 + r;
            }
        }

        /// <summary>Height (mm above the floor) where Revit cuts plan views by default.</summary>
        public const double AlturaCorte = 1200;

        /// <summary>Handles only on doors (windows never get one).</summary>
        private bool Puxadores => _c.Puxadores && _c.IsPorta;

        /// <summary>Voids of the family (cut the host wall): the recess of the window sill in the masonry.</summary>
        public readonly List<Prisma> Vazios = new List<Prisma>();

        public static Layout Build(EsquadriaConfig cfg)
        {
            var l = new Layout();
            l.Run(cfg.Clone());
            return l;
        }

        private void Run(EsquadriaConfig c)
        {
            _c = c;
            if (c.ParedeCortina)
            {
                // the "wall" of a curtain-wall esquadria is its own frame (c is a copy)
                c.ParedeMm = c.MarcoProfMm;
                c.RevestimentoExtMm = 0;
                c.RevestimentoIntMm = 0;
            }
            _vidro = Catalog.Vidro(c.VidroId);
            W = c.LarguraMm;
            H = c.AlturaMm;
            _mf = Math.Min(c.MarcoFaceMm, Math.Min(W, H) / 4);
            _mp = c.MarcoNaParede ? c.ParedeMm + c.RevestimentoExtMm + c.RevestimentoIntMm : c.MarcoProfMm;
            _ff = c.FolhaFaceMm;
            _fp = c.FolhaProfMm;
            _tv = c.TravessaMm;
            _bottom = c.IsPorta ? 0 : _mf;
            ParedeExt = FrameOffsetFromCenter(c) - c.ParedeMm / 2;
            ParedeInt = ParedeExt + c.ParedeMm;
            FaceExt = ParedeExt - c.RevestimentoExtMm;
            FaceInt = ParedeInt + c.RevestimentoIntMm;

            // ------------------------------------------------------------ frame (marco)
            if (c.IsPorta)
            {
                var u = new Prisma { D0 = 0, D1 = _mp, Papel = Papel.Marco, Material = MaterialSlot.Perfil };
                u.Loops.Add(new List<P2>
                {
                    new P2(0, 0), new P2(_mf, 0), new P2(_mf, H - _mf), new P2(W - _mf, H - _mf),
                    new P2(W - _mf, 0), new P2(W, 0), new P2(W, H), new P2(0, H)
                });
                Prismas.Add(u);
            }
            else
            {
                Prismas.Add(Prisma.Ring(new Rc(0, 0, W, H), new Rc(_mf, _mf, W - _mf, H - _mf), 0, _mp, Papel.Marco, MaterialSlot.Perfil));
            }

            // ------------------------------------------------------------ rows and modules
            var rows = c.Linhas;
            double[] heights = Distribute(H, rows.Select(r => r.AlturaMm).ToArray(), "linhas");
            // the row cut by the plan view: doors = bottom row; windows = row at 1.20 m (or the bottom one)
            {
                double yCut = c.IsPorta ? 0 : AlturaCorte - c.PeitorilMm;
                double top = H;
                _cutRow = rows.Count - 1;
                for (int r = 0; r < rows.Count; r++)
                {
                    double bottom = r == rows.Count - 1 ? 0 : top - heights[r];
                    if (!c.IsPorta && yCut >= bottom && yCut <= top) { _cutRow = r; break; }
                    top = bottom;
                }
            }

            double yTop = H;
            for (int r = 0; r < rows.Count; r++)
            {
                double yt = yTop, yb = r == rows.Count - 1 ? 0 : yTop - heights[r];
                yTop = yb;
                if (r < rows.Count - 1)
                {
                    // transom between this row and the next one
                    Prismas.Add(Prisma.Box(new Rc(_mf, yb - _tv / 2, W - _mf, yb + _tv / 2), 0, _mp, Papel.Travessa, MaterialSlot.Perfil));
                }

                var cells = rows[r].Celulas;
                double[] widths = Distribute(W, cells.Select(x => x.LarguraMm).ToArray(), "colunas da linha " + (r + 1));
                double xL = 0;
                double luzY0 = yb <= 0.001 ? _bottom : yb + _tv / 2;
                double luzY1 = yt >= H - 0.001 ? H - _mf : yt - _tv / 2;
                for (int k = 0; k < cells.Count; k++)
                {
                    double xl = xL, xr = k == cells.Count - 1 ? W : xL + widths[k];
                    xL = xr;
                    if (k < cells.Count - 1)
                    {
                        Prismas.Add(Prisma.Box(new Rc(xr - _tv / 2, luzY0, xr + _tv / 2, luzY1), 0, _mp, Papel.Travessa, MaterialSlot.Perfil));
                        if (r == _cutRow) { G(GrupoPlanta.Marco, c.PlantaMarco); PlanRect(xr - _tv / 2, 0, xr + _tv / 2, _mp, false); }
                    }

                    var luz = new Rc(xl <= 0.001 ? _mf : xl + _tv / 2, luzY0, xr >= W - 0.001 ? W - _mf : xr - _tv / 2, luzY1);
                    var m = new Modulo { Row = r, Col = k, Mod = new Rc(xl, yb, xr, yt), Luz = luz, Celula = cells[k] };
                    Modulos.Add(m);
                    if (!luz.Valid)
                    {
                        Avisos.Add($"Módulo L{r + 1}C{k + 1} pequeno demais para o perfil escolhido.");
                        continue;
                    }
                    BuildCell(m);
                }
            }

            // ------------------------------------------------------------ door: stops, casings
            if (c.IsPorta)
            {
                DoorStops();
                Casings();
            }

            // ------------------------------------------------------------ sill / threshold
            if (!c.IsPorta && c.Peitoril && !c.ParedeCortina)
            {
                double aba = c.PeitorilAbaMm;
                double d0 = FaceExt - c.PeitorilProjecaoMm;
                double d1 = c.PeitorilLarguraMm > 0 ? d0 + c.PeitorilLarguraMm : FaceInt;
                if (d1 < _mp) d1 = Math.Max(d1, d0 + 10);
                double ep = c.PeitorilEspessuraMm;
                // drip groove: 8 mm wide, a third of the stone deep, 15 mm in from the outer edge,
                // only on the projecting part (the stone is split there: front, groove, rest)
                double g0 = d0 + 15, g1 = g0 + 8, gh = Math.Max(3, Math.Min(6, ep / 3));
                if (c.PeitorilPingadeira && c.PeitorilProjecaoMm >= 25 && g1 < d1 - 10)
                {
                    Prismas.Add(Prisma.Box(new Rc(-aba, -ep, W + aba, 0), d0, g0, Papel.Peitoril, MaterialSlot.Peitoril));
                    Prismas.Add(Prisma.Box(new Rc(-aba, -ep + gh, W + aba, 0), g0, g1, Papel.Peitoril, MaterialSlot.Peitoril));
                    Prismas.Add(Prisma.Box(new Rc(-aba, -ep, W + aba, 0), g1, d1, Papel.Peitoril, MaterialSlot.Peitoril));
                }
                else
                {
                    if (c.PeitorilPingadeira) Avisos.Add("Pingadeira: precisa de pelo menos 25 mm de projeção do peitoril para fora.");
                    Prismas.Add(Prisma.Box(new Rc(-aba, -ep, W + aba, 0), d0, d1, Papel.Peitoril, MaterialSlot.Peitoril));
                }
                Pedra = new Rc(-aba, d0, W + aba, d1);
                // the stone sits in the masonry under the opening (and its abas beside it): cut the wall there
                Vazios.Add(Prisma.Box(new Rc(-aba - 1, -c.PeitorilEspessuraMm - 1, W + aba + 1, 1), d0 - 1, d1 + 1, Papel.Peitoril, MaterialSlot.Peitoril));
            }
            if (c.IsPorta && c.Soleira)
            {
                double len = c.SoleiraComprimentoMm > 0 ? c.SoleiraComprimentoMm : W - 2 * _mf;
                double wid = c.SoleiraLarguraMm > 0 ? c.SoleiraLarguraMm : FaceInt - FaceExt;
                double s0, s1;
                switch (c.SoleiraAlinhamento)
                {
                    // flush with the esquadria's exterior / interior side (frame faces)
                    case AlinhamentoSoleira.FaceExterna: s0 = 0; s1 = wid; break;
                    case AlinhamentoSoleira.FaceInterna: s1 = _mp; s0 = _mp - wid; break;
                    default:
                        double dm = (FaceExt + FaceInt) / 2;
                        s0 = dm - wid / 2; s1 = dm + wid / 2; break;
                }
                var r = new Rc((W - len) / 2, -c.SoleiraEspessuraMm, (W + len) / 2, 0);
                Prismas.Add(Prisma.Box(r, s0, s1, Papel.Soleira, MaterialSlot.Peitoril));
                Pedra = new Rc(r.X0, s0, r.X1, s1);
            }

            // ------------------------------------------------------------ sub-frame (contramarco)
            // outside the opening (the vão stays W × H): jambs, head and, on windows without sill,
            // the bottom; it sits in a pocket cut in the wall
            if (c.Contramarco && !c.ParedeCortina)
            {
                double cw = c.ContramarcoLarguraMm, cd = c.ContramarcoProfMm > 0 ? Math.Min(c.ContramarcoProfMm, _mp) : _mp;
                double cd0 = (_mp - cd) / 2, cd1 = cd0 + cd;
                bool fundo = !c.IsPorta && !c.Peitoril;
                double yb = fundo ? -cw : 0;
                var partes = new List<Rc> { new Rc(-cw, yb, 0, H + cw), new Rc(W, yb, W + cw, H + cw), new Rc(0, H, W, H + cw) };
                if (fundo) partes.Add(new Rc(0, -cw, W, 0));
                foreach (Rc r in partes)
                {
                    Prismas.Add(Prisma.Box(r, cd0, cd1, Papel.Marco, MaterialSlot.Perfil));
                    Vazios.Add(Prisma.Box(new Rc(r.X0 - 1, r.Y0 - 1, r.X1 + 1, r.Y1 + 1), cd0 - 1, cd1 + 1, Papel.Marco, MaterialSlot.Perfil));
                }
                G(GrupoPlanta.Marco, c.PlantaMarco);
                PlanRect(-cw, cd0, 0, cd1, true);
                PlanRect(W, cd0, W + cw, cd1, true);
            }

            // ------------------------------------------------------------ plan (2D)
            PlanFrameAndStone();
            if (!c.IsPorta) WindowPlanSection();

            // ------------------------------------------------------------ numbers
            AreaTotal = W * H / 1e6;
            double ag = AreaVidro, ap = _panelArea / 1e6, al = _louverArea / 1e6;
            AreaOpaca = ap + al;
            double af = Math.Max(0, AreaTotal - ag - ap - al);
            var perfil = Catalog.Perfil(c.PerfilId);
            const double upPanel = 2.0, upLouver = 5.0;
            Uw = AreaTotal > 0
                ? (ag * _vidro.Ug + af * perfil.Uf + ap * upPanel + al * upLouver + (_glassPerimeter / 1000.0) * _vidro.Psi) / AreaTotal
                : 0;
        }

        // ---------------------------------------------------------------- depth bands
        private double SashD0 => _fp <= _mp ? (_mp - _fp) / 2 : 0;
        private double SashD1 => SashD0 + _fp;

        /// <summary>Depth band of a hinged leaf. Doors with the frame across the wall: the leaf sits
        /// against the face it opens to (stop on the other side); otherwise centred in the frame.</summary>
        private void LeafBand(bool inward, out double d0, out double d1)
        {
            if (_c.MarcoNaParede && _fp < _mp - 8)
            {
                if (inward) { d1 = _mp - 4; d0 = d1 - _fp; }
                else { d0 = 4; d1 = d0 + _fp; }
                return;
            }
            d0 = SashD0;
            d1 = SashD1;
        }

        private static bool IsHinged(Abertura a) =>
            a == Abertura.AbrirEsquerda || a == Abertura.AbrirDireita || a == Abertura.AbrirDupla ||
            a == Abertura.OsciloBatenteEsquerda || a == Abertura.OsciloBatenteDireita || a == Abertura.Pivotante;

        // ---------------------------------------------------------------- cells
        private void BuildCell(Modulo m)
        {
            var cell = m.Celula;
            var L = m.Luz;
            bool porta = _c.IsPorta;
            bool plan = m.Row == _cutRow;
            bool doorBottom = porta && L.Y0 <= 0.001;
            // a door leaf stops a little above the floor
            var S = doorBottom ? new Rc(L.X0, Math.Min(_c.FrestaMm, L.H / 4), L.X1, L.Y1) : L;
            bool inward = !cell.AbreParaFora;
            LeafBand(inward, out double ld0, out double ld1);
            bool doorLeafPlan = porta && plan && doorBottom;

            switch (cell.Abertura)
            {
                case Abertura.Fixo:
                    Fill(L, cell.Preenchimento, 0, _mp, true, 1.0, plan);
                    if (_c.MarcarFixo && cell.Preenchimento == Preenchimento.Vidro) LetterF(L);
                    break;

                case Abertura.AbrirDupla:
                {
                    var a = new Rc(S.X0, S.Y0, S.Xm, S.Y1);
                    var b = new Rc(S.Xm, S.Y0, S.X1, S.Y1);
                    Sash(a, cell.Preenchimento, ld0, ld1, plan && !doorLeafPlan);
                    Sash(b, cell.Preenchimento, ld0, ld1, plan && !doorLeafPlan);
                    HingeSymbol(a, true, inward);
                    HingeSymbol(b, false, inward);
                    if (doorLeafPlan) { PlanSwing(a, true, inward, ld0, ld1); PlanSwing(b, false, inward, ld0, ld1); }
                    Handle(a, false, ld0, ld1);
                    Handle(b, true, ld0, ld1);
                    AreaVentilacao += L.Area / 1e6;
                    break;
                }

                case Abertura.Correr:
                    Sliding(L, S, cell, plan);
                    break;

                default:
                {
                    Sash(S, cell.Preenchimento, ld0, ld1, plan && !doorLeafPlan);
                    switch (cell.Abertura)
                    {
                        case Abertura.AbrirEsquerda:
                            HingeSymbol(S, true, inward); if (doorLeafPlan) PlanSwing(S, true, inward, ld0, ld1); Handle(S, false, ld0, ld1); break;
                        case Abertura.AbrirDireita:
                            HingeSymbol(S, false, inward); if (doorLeafPlan) PlanSwing(S, false, inward, ld0, ld1); Handle(S, true, ld0, ld1); break;
                        case Abertura.OsciloBatenteEsquerda:
                            HingeSymbol(S, true, true); TiltSymbol(S); if (doorLeafPlan) PlanSwing(S, true, true, ld0, ld1); Handle(S, false, ld0, ld1); break;
                        case Abertura.OsciloBatenteDireita:
                            HingeSymbol(S, false, true); TiltSymbol(S); if (doorLeafPlan) PlanSwing(S, false, true, ld0, ld1); Handle(S, true, ld0, ld1); break;
                        case Abertura.MaximAr:
                            // hinge on top: lines from the top corners to the middle of the free (bottom) side
                            Line(S.X0, S.Y1, S.Xm, S.Y0, false); Line(S.X1, S.Y1, S.Xm, S.Y0, false);
                            BottomHandle(S); break;
                        case Abertura.Tombar:
                            TiltSymbol(S); TopHandle(S); break;
                        case Abertura.Basculante:
                            Line(S.X0, S.Ym, S.Xm, S.Y1, false); Line(S.Xm, S.Y1, S.X1, S.Ym, false);
                            Line(S.X0, S.Ym, S.Xm, S.Y0, true); Line(S.Xm, S.Y0, S.X1, S.Ym, true);
                            Axis(S.X0, S.Ym, S.X1, S.Ym);
                            BottomHandle(S); break;
                        case Abertura.Pivotante:
                            Line(S.Xm, S.Y1, S.X0, S.Ym, false); Line(S.X0, S.Ym, S.Xm, S.Y0, false);
                            Line(S.Xm, S.Y1, S.X1, S.Ym, true); Line(S.X1, S.Ym, S.Xm, S.Y0, true);
                            Axis(S.Xm, S.Y0, S.Xm, S.Y1);
                            if (doorLeafPlan) PlanPivot(S, ld0, ld1);
                            Handle(S, false, ld0, ld1); break;
                    }
                    double factor = cell.Abertura == Abertura.Basculante ? 0.9 : 1.0;
                    AreaVentilacao += L.Area * factor / 1e6;
                    break;
                }
            }
        }

        /// <summary>Sash (folha): ring of sash profile + its infill.</summary>
        private void Sash(Rc s, Preenchimento p, double d0, double d1, bool plan)
        {
            NumFolhas++;
            double f = Math.Min(_ff, Math.Min(s.W, s.H) / 3);
            bool coarse = !_c.SimplificarBaixo;
            Prismas.Add(Prisma.Ring(s, s.Inset(f), d0, d1, Papel.Folha, MaterialSlot.Perfil).Lod(coarse, true, true));
            Fill(s.Inset(f), p, d0, d1, false, 1.0, false);
            if (plan)
            {
                // closed leaf seen in plan (windows)
                G(GrupoPlanta.Folha, _c.PlantaFolhas);
                PlanRect(s.X0, d0, s.X1, d1, false);
            }
        }

        /// <summary>Glass, louvres or a blind panel inside a rectangle, centred in the depth band.</summary>
        private void Fill(Rc r, Preenchimento p, double d0, double d1, bool inFrame, double areaFactor, bool plan)
        {
            if (!r.Valid) return;
            if (plan) G(GrupoPlanta.Folha, _c.PlantaFolhas);
            double mid = (d0 + d1) / 2;
            double band = d1 - d0;
            switch (p)
            {
                case Preenchimento.Vidro:
                {
                    double g = Math.Max(3, Math.Min(_vidro.Espessura, band - 4));
                    Prismas.Add(Prisma.Box(r, mid - g / 2, mid + g / 2, Papel.Vidro, MaterialSlot.Vidro));
                    AreaVidro += r.Area * areaFactor / 1e6;
                    AreaIluminacao += r.Area * areaFactor / 1e6;
                    _glassPerimeter += 2 * (r.W + r.H) * areaFactor;
                    if (plan) { PlanLine(r.X0, mid - g / 2, r.X1, mid - g / 2, true); PlanLine(r.X0, mid + g / 2, r.X1, mid + g / 2, true); }
                    break;
                }
                case Preenchimento.Veneziana:
                {
                    _louverArea += r.Area * areaFactor;
                    double t = Math.Max(6, band - 6);
                    // coarse: one thin panel; medium/fine: the slats
                    Prismas.Add(Prisma.Box(r, mid - 3, mid + 3, Papel.Veneziana, MaterialSlot.Perfil).Lod(true, false, false));
                    const double pitch = 60, slat = 42;
                    int n = Math.Max(1, Math.Min(80, (int)Math.Floor(r.H / pitch)));
                    double gap = (r.H - n * slat) / (n + 1);
                    for (int i = 0; i < n; i++)
                    {
                        double y0 = r.Y0 + gap + i * (slat + gap);
                        Prismas.Add(Prisma.Box(new Rc(r.X0, y0, r.X1, y0 + slat), mid - t / 2, mid + t / 2, Papel.Veneziana, MaterialSlot.Perfil).Lod(false, true, true));
                    }
                    if (inFrame) AreaVentilacao += r.Area * areaFactor * 0.5 / 1e6;
                    if (plan) PlanRect(r.X0, mid - t / 2, r.X1, mid + t / 2, true);
                    break;
                }
                case Preenchimento.Cego:
                {
                    _panelArea += r.Area * areaFactor;
                    double t = Math.Max(10, Math.Min(band, Math.Max(20, band * 0.7)));
                    Prismas.Add(Prisma.Box(r, mid - t / 2, mid + t / 2, Papel.Painel, MaterialSlot.Painel));
                    if (plan) PlanRect(r.X0, mid - t / 2, r.X1, mid + t / 2, false);
                    break;
                }
            }
        }

        private static readonly Dictionary<int, int[]> Tracks = new Dictionary<int, int[]>
        {
            [2] = new[] { 0, 1 },
            [3] = new[] { 0, 1, 2 },
            [4] = new[] { 0, 1, 1, 0 },
            [6] = new[] { 0, 1, 2, 2, 1, 0 },
        };

        /// <summary>One leaf of a sliding module: where it is, its track (0 = exterior), where it
        /// moves to open (−1 left, +1 right, 0 both), fixed or not, and the edge with the handle.</summary>
        private struct FolhaCorrer
        {
            public Rc R;
            public int Trilho, Dir;
            public bool Fixa, PuxadorDireita;
        }

        /// <summary>
        /// Leaves of a sliding module by mode. Padrão (as before): overlapping leaves on the classic
        /// tracks, the outer ones move to the centre. Central: half of the leaves on each side, the
        /// two in the middle meet at the centre and move outwards over the others; with 4 or 6
        /// leaves the end leaves are fixed. Recolhe: one fixed leaf at one side and each moving
        /// leaf on its own track, all stacking onto the fixed one.
        /// </summary>
        private List<FolhaCorrer> FolhasCorrer(Rc S, Celula cell, out int nTracks, out double abre)
        {
            var list = new List<FolhaCorrer>();
            int n = Tracks.ContainsKey(cell.Folhas) ? cell.Folhas : 2;
            switch (cell.Modo)
            {
                case ModoCorrer.Central:
                {
                    if (n % 2 == 1) n++;
                    int half = n / 2;
                    double f = Math.Min(_ff, S.W / (n * 3));
                    double lw = (S.W / 2 + (half - 1) * f) / half;
                    for (int side = 0; side < 2; side++)
                        for (int k = 0; k < half; k++)
                        {
                            bool left = side == 0;
                            double x0 = left ? S.X0 + k * (lw - f) : S.X1 - k * (lw - f) - lw;
                            list.Add(new FolhaCorrer
                            {
                                R = new Rc(x0, S.Y0, x0 + lw, S.Y1),
                                Trilho = k,
                                Fixa = half >= 2 && k == 0,
                                Dir = left ? -1 : 1,
                                PuxadorDireita = left          // the edge towards the centre
                            });
                        }
                    nTracks = half;
                    abre = half == 1 ? 1.0 : (half - 1.0) / half;
                    if (half == 1) Avisos.Add("Abertura central com 2 folhas: elas correm para fora do vão (por cima da parede ou embutidas); use 4 folhas para ter as das pontas fixas.");
                    break;
                }
                case ModoCorrer.Recolhe:
                {
                    double f = Math.Min(_ff, S.W / (n * 3));
                    double lw = (S.W + (n - 1) * f) / n;
                    bool dir = cell.RecolheDireita;
                    for (int j = 0; j < n; j++)
                    {
                        double x0 = dir ? S.X1 - j * (lw - f) - lw : S.X0 + j * (lw - f);
                        list.Add(new FolhaCorrer
                        {
                            R = new Rc(x0, S.Y0, x0 + lw, S.Y1),
                            Trilho = j,
                            Fixa = j == 0,
                            Dir = dir ? 1 : -1,
                            PuxadorDireita = !dir          // the edge away from the stack
                        });
                    }
                    nTracks = n;
                    abre = (n - 1.0) / n;
                    break;
                }
                default:
                {
                    int[] track = Tracks[n];
                    double f = Math.Min(_ff, S.W / (n * 3));
                    double lw = (S.W + (n - 1) * f) / n;
                    for (int i = 0; i < n; i++)
                    {
                        double x0 = S.X0 + i * (lw - f);
                        var leaf = new Rc(x0, S.Y0, x0 + lw, S.Y1);
                        // direction: outer leaves move towards the centre, inner ones both ways (▷ ▷◁ ▷◁ ◁)
                        bool middle = !(i == 0 || i == n - 1);
                        bool toRight = leaf.Xm < S.Xm;
                        list.Add(new FolhaCorrer
                        {
                            R = leaf,
                            Trilho = track[i],
                            Dir = middle ? 0 : (toRight ? 1 : -1),
                            PuxadorDireita = toRight || middle
                        });
                    }
                    nTracks = track.Max() + 1;
                    abre = n == 3 ? 2.0 / 3.0 : 0.5;
                    break;
                }
            }
            return list;
        }

        private void Sliding(Rc L, Rc S, Celula cell, bool plan)
        {
            List<FolhaCorrer> folhas = FolhasCorrer(S, cell, out int nTracks, out double abre);
            int n = folhas.Count;
            double f = Math.Min(_ff, S.W / (n * 3));
            // tracks inside the frame depth (a door frame across the wall keeps the tracks in a
            // band of the profile depth, centred)
            double band = _c.MarcoNaParede ? Math.Min(_mp, Math.Max(_c.MarcoProfMm, nTracks * (_fp + 4))) : _mp;
            double b0 = (_mp - band) / 2;
            double tw = band / nTracks;
            double leafDepth = Math.Max(8, Math.Min(_fp, tw - 2));
            if (cell.Modo != ModoCorrer.Padrao && leafDepth < _fp * 0.6)
                Avisos.Add($"{nTracks} trilhos em {Mm(band)} mm de marco: as folhas ficaram com {Mm(leafDepth)} mm de profundidade (aumente a profundidade do marco ou use menos folhas).");
            double scale = S.W / Math.Max(1, folhas.Sum(x => x.R.W)); // overlapping leaves: areas counted once
            bool coarse = !_c.SimplificarBaixo;

            foreach (var fo in folhas)
            {
                var leaf = fo.R;
                double c0 = b0 + tw * (fo.Trilho + 0.5);
                double d0 = c0 - leafDepth / 2, d1 = c0 + leafDepth / 2;
                NumFolhas++;
                Prismas.Add(Prisma.Ring(leaf, leaf.Inset(f), d0, d1, Papel.Folha, MaterialSlot.Perfil).Lod(coarse, true, true));
                var p = cell.MistoVeneziana && fo.Trilho % 2 == 0 ? Preenchimento.Veneziana : cell.Preenchimento;
                Fill(leaf.Inset(f), p, d0, d1, false, scale, false);
                if (plan)
                {
                    G(GrupoPlanta.Folha, _c.PlantaFolhas);
                    PlanRect(leaf.X0, d0, leaf.X1, d1, false);
                }

                if (fo.Fixa)
                {
                    // fixed sliding leaf: "F" (office standard), no arrow, no handle
                    if (_c.MarcarFixo && p == Preenchimento.Vidro) LetterF(leaf.Inset(f));
                    continue;
                }
                if (_c.ElevacaoCorrer == SimboloCorrer.Setas)
                    Arrow(leaf.Xm, leaf.Y0 + leaf.H * 0.32, Math.Min(leaf.W * 0.45, 400), fo.Dir);
                else
                    SlideTriangles(leaf.Xm, leaf.Ym, Math.Min(140, Math.Min(leaf.W, leaf.H) * 0.18), fo.Dir);

                // plan: small arrow beside the leaf, on the side of its track
                if (plan && (_c.PlantaSetasCorrer ?? _c.IsPorta))
                {
                    double off = fo.Trilho < nTracks / 2.0 ? d0 - 70 : d1 + 70;
                    double alen = Math.Min(400, leaf.W * 0.3), hh = Math.Min(50, alen * 0.2);
                    double ax0 = leaf.Xm - alen / 2, ax1 = leaf.Xm + alen / 2;
                    G(GrupoPlanta.Setas, true);
                    PlanLine(ax0, off, ax1, off, true);
                    if (fo.Dir >= 0) { PlanLine(ax1, off, ax1 - hh, off - hh * 0.5, true); PlanLine(ax1, off, ax1 - hh, off + hh * 0.5, true); }
                    if (fo.Dir <= 0) { PlanLine(ax0, off, ax0 + hh, off - hh * 0.5, true); PlanLine(ax0, off, ax0 + hh, off + hh * 0.5, true); }
                }

                if (Puxadores) VerticalHandle(fo.PuxadorDireita ? leaf.X1 - f / 2 : leaf.X0 + f / 2, leaf, d0, d1);
            }
            AreaVentilacao += L.Area * abre / 1e6;
        }

        // ---------------------------------------------------------------- doors: stop, casings
        /// <summary>Stops (batentes) along the jambs and the head of each hinged door module.</summary>
        private void DoorStops()
        {
            if (!_c.MarcoNaParede || _c.BatenteMm <= 0) return;
            double b = _c.BatenteMm;
            foreach (var m in Modulos.Where(x => IsHinged(x.Celula.Abertura) && x.Luz.Y0 <= 0.001 && x.Luz.Valid))
            {
                bool inward = !m.Celula.AbreParaFora;
                LeafBand(inward, out double ld0, out double ld1);
                double d0 = inward ? 0 : ld1, d1 = inward ? ld0 : _mp;
                if (d1 - d0 < 2) continue;
                var L = m.Luz;
                Prismas.Add(Prisma.Box(new Rc(L.X0, 0, L.X0 + b, L.Y1), d0, d1, Papel.Batente, MaterialSlot.Perfil));
                Prismas.Add(Prisma.Box(new Rc(L.X1 - b, 0, L.X1, L.Y1), d0, d1, Papel.Batente, MaterialSlot.Perfil));
                Prismas.Add(Prisma.Box(new Rc(L.X0 + b, L.Y1 - b, L.X1 - b, L.Y1), d0, d1, Papel.Batente, MaterialSlot.Perfil));
            }
        }

        /// <summary>Casings (guarnições/alizares) on both wall faces, around jambs and head.</summary>
        private void Casings()
        {
            if (!_c.Guarnicoes || !_c.MarcoNaParede) return;
            double gw = _c.GuarnicaoLarguraMm, gt = _c.GuarnicaoEspessuraMm, set = 5;
            double xi0 = _mf - set, xi1 = W - _mf + set;           // inner edges
            double yh = H - _mf + set;                              // inner edge of the head casing
            foreach (bool exterior in new[] { true, false })
            {
                double d0 = exterior ? FaceExt - gt : FaceInt, d1 = exterior ? FaceExt : FaceInt + gt;
                Prismas.Add(Prisma.Box(new Rc(xi0 - gw, 0, xi0, yh), d0, d1, Papel.Guarnicao, MaterialSlot.Perfil));
                Prismas.Add(Prisma.Box(new Rc(xi1, 0, xi1 + gw, yh), d0, d1, Papel.Guarnicao, MaterialSlot.Perfil));
                Prismas.Add(Prisma.Box(new Rc(xi0 - gw, yh, xi1 + gw, yh + gw), d0, d1, Papel.Guarnicao, MaterialSlot.Perfil));
                G(GrupoPlanta.Guarnicao, _c.PlantaGuarnicoes);
                PlanRect(xi0 - gw, d0, xi0, d1, false);
                PlanRect(xi1, d0, xi1 + gw, d1, false);
            }
        }

        // ---------------------------------------------------------------- plan (2D)
        private void PlanLine(double x0, double d0, double x1, double d1, bool thin)
        {
            if (!_c.SimbolosPlanta) return;
            if (Math.Abs(x1 - x0) + Math.Abs(d1 - d0) < 0.5) return;
            Planta.Add(new TracoPlanta { A = new P2(x0, d0), B = new P2(x1, d1), Fino = thin || _grpDash, Tracejado = _grpDash, Grupo = _grp, Visivel = _grpVis });
        }

        /// <summary>
        /// Windows: the plan IS the horizontal section of the solids at the plan cut (1.20 m from the
        /// floor, like the family editor shows it): what is cut is drawn with the cut outline, what
        /// is below the cut (bottom frame member, sash rails, lower panes) in projection. The sill
        /// stone and the handles are left out. Same drawing whatever the cut plane of the project
        /// view, because Revit shows these lines instead of the solids.
        /// </summary>
        private void WindowPlanSection()
        {
            Planta.RemoveAll(t => t.Grupo == GrupoPlanta.Marco || t.Grupo == GrupoPlanta.Folha || t.Grupo == GrupoPlanta.FolhaFechada || t.Grupo == GrupoPlanta.Pedra);
            if (!_c.SimbolosPlanta) return;

            // the opening in the wall (jambs across the wall): drawn always, so a window that is not
            // reached by the cut plane of the view (high sill, e.g. bathroom windows) still shows its
            // opening; the family also masks the wall between the jambs
            if (!_c.ParedeCortina)
            {
                G(GrupoPlanta.Vao, true);
                PlanLine(0, ParedeExt, 0, ParedeInt, false);
                PlanLine(W, ParedeExt, W, ParedeInt, false);
            }

            double h = AlturaCorte - _c.PeitorilMm;
            if (h <= 1 || h >= H - 1)
            {
                var m = Modulos.FirstOrDefault(x => x.Row == _cutRow) ?? Modulos.FirstOrDefault();
                h = m != null ? m.Luz.Ym : H / 2;
            }
            // never exactly on a horizontal edge
            for (int k = 0; k < 20 && Prismas.Any(p => p.Loops.Any(l => l.Any(q => Math.Abs(q.Y - h) < 0.6))); k++) h += 1.7;

            foreach (var p in Prismas)
            {
                if (p.Papel == Papel.Peitoril || p.Papel == Papel.Puxador || p.Papel == Papel.Soleira || p.Loops.Count == 0 || p.Loops[0].Count < 3) continue;
                if (!p.Medium) continue;   // coarse-only stand-ins (e.g. louvre panel)
                G(p.Papel == Papel.Marco || p.Papel == Papel.Travessa ? GrupoPlanta.Marco : GrupoPlanta.Folha, true);
                bool thin = p.Papel == Papel.Vidro;
                double ymin = p.Loops[0].Min(q => q.Y), ymax = p.Loops[0].Max(q => q.Y);
                if (ymin > h) continue;                                   // above the cut: not seen
                if (ymax < h)
                {
                    // below the cut: seen from above (its extent along the wall)
                    PlanRect(p.Loops[0].Min(q => q.X), p.D0, p.Loops[0].Max(q => q.X), p.D1, true);
                    continue;
                }
                foreach (var iv in Crossings(p.Loops, h)) PlanRect(iv.Key, p.D0, iv.Value, p.D1, thin);
                // leaves: the bottom rail below the cut closes the outline of each leaf (as in the family editor)
                if (p.Papel == Papel.Folha) PlanRect(p.Loops[0].Min(q => q.X), p.D0, p.Loops[0].Max(q => q.X), p.D1, true);
            }
        }

        /// <summary>X intervals where the horizontal line y = h is inside the profile (even-odd over all loops).</summary>
        private static List<KeyValuePair<double, double>> Crossings(List<List<P2>> loops, double h)
        {
            var xs = new List<double>();
            foreach (var l in loops)
                for (int i = 0; i < l.Count; i++)
                {
                    P2 a = l[i], b = l[(i + 1) % l.Count];
                    if ((a.Y <= h && b.Y > h) || (b.Y <= h && a.Y > h))
                        xs.Add(a.X + (h - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                }
            xs.Sort();
            var r = new List<KeyValuePair<double, double>>();
            for (int i = 0; i + 1 < xs.Count; i += 2)
                if (xs[i + 1] - xs[i] > 0.5) r.Add(new KeyValuePair<double, double>(xs[i], xs[i + 1]));
            return r;
        }

        private void PlanRect(double x0, double d0, double x1, double d1, bool thin)
        {
            PlanLine(x0, d0, x1, d0, thin);
            PlanLine(x1, d0, x1, d1, thin);
            PlanLine(x1, d1, x0, d1, thin);
            PlanLine(x0, d1, x0, d0, thin);
        }

        private void PlanPoly(List<P2> pts, bool thin)
        {
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                PlanLine(a.X, a.Y, b.X, b.Y, thin);
            }
        }

        private void PlanArc(P2 a, P2 b, P2 mid)
        {
            if (!_c.SimbolosPlanta) return;
            Planta.Add(new TracoPlanta { A = a, B = b, Mid = mid, IsArc = true, Fino = true, Tracejado = _grpDash, Grupo = _grp, Visivel = _grpVis });
        }

        /// <summary>
        /// Jambs of the frame in plan (doors: with the stop step where a hinged leaf closes),
        /// the stone (peitoril/soleira) outline and, for windows, the edges of the opening seen
        /// on the sill below the cut.
        /// </summary>
        private void PlanFrameAndStone()
        {
            var cutCells = Modulos.Where(m => m.Row == _cutRow).OrderBy(m => m.Col).ToList();
            G(GrupoPlanta.Marco, _c.PlantaMarco);
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0;
                Func<double, double> X = x => left ? x : W - x;
                var cell = cutCells.Count == 0 ? null : (left ? cutCells[0] : cutCells[cutCells.Count - 1]);
                bool stop = _c.IsPorta && _c.MarcoNaParede && _c.BatenteMm > 0 && cell != null && IsHinged(cell.Celula.Abertura);
                var pts = new List<P2>();
                if (stop)
                {
                    bool inward = !cell.Celula.AbreParaFora;
                    LeafBand(inward, out double ld0, out double ld1);
                    double b = _c.BatenteMm, sd = inward ? ld0 : ld1;
                    if (inward)
                        pts.AddRange(new[] { new P2(X(0), 0), new P2(X(_mf + b), 0), new P2(X(_mf + b), sd), new P2(X(_mf), sd), new P2(X(_mf), _mp), new P2(X(0), _mp) });
                    else
                        pts.AddRange(new[] { new P2(X(0), 0), new P2(X(_mf), 0), new P2(X(_mf), sd), new P2(X(_mf + b), sd), new P2(X(_mf + b), _mp), new P2(X(0), _mp) });
                }
                else
                {
                    pts.AddRange(new[] { new P2(X(0), 0), new P2(X(_mf), 0), new P2(X(_mf), _mp), new P2(X(0), _mp) });
                }
                PlanPoly(pts, false);
            }

            // windows: the bottom member of the frame (marco inferior) is seen below the cut, across
            // the whole opening between the jambs
            if (!_c.IsPorta)
            {
                PlanLine(_mf, 0, W - _mf, 0, false);
                PlanLine(_mf, _mp, W - _mf, _mp, false);
            }

            // projection of the head (verga) above the cut: dashed lines on the finished faces
            if (!_c.ParedeCortina)
            {
                G(GrupoPlanta.Verga, _c.PlantaVerga, true);
                PlanLine(0, FaceExt, W, FaceExt, true);
                PlanLine(0, FaceInt, W, FaceInt, true);
            }

            if (Pedra.HasValue)
            {
                G(GrupoPlanta.Pedra, _c.PlantaPedra);
                var p = Pedra.Value;
                PlanRect(p.X0, p.Y0, p.X1, p.Y1, false);
                if (!_c.IsPorta && p.Y1 > _mp + 1)
                {
                    // edges of the opening (vão) on the sill, between the frame and the inner edge of the stone
                    PlanLine(0, _mp, 0, p.Y1, false);
                    PlanLine(W, _mp, W, p.Y1, false);
                }
            }
        }

        /// <summary>
        /// Door leaf drawn OPEN (rectangle of the leaf thickness, or a line) at the chosen angle,
        /// starting on the frame face (the outermost structure line) or on the hinge axis, with the
        /// swing as an arc, a dashed arc or a straight line; optionally the closed leaf, dashed.
        /// </summary>
        private void PlanSwing(Rc s, bool hingeLeft, bool inward, double ld0, double ld1)
        {
            double w = s.W, t = ld1 - ld0;
            double sx = hingeLeft ? 1 : -1, sd = inward ? 1 : -1;
            double hx = hingeLeft ? s.X0 : s.X1;            // hinge line
            double hd = _c.PlantaAlinhamento == AlinhamentoFolha.FaceDoMarco
                ? (inward ? _mp : 0)                        // flush with the frame face
                : (inward ? ld1 : ld0);                     // on the hinge axis
            double a = _c.PlantaAngulo * Math.PI / 180;
            // leaf direction (towards the tip) and thickness direction (towards the opening)
            var u = new P2(sx * Math.Cos(a), sd * Math.Sin(a));
            var v = new P2(sx * Math.Sin(a), -sd * Math.Cos(a));
            var h = new P2(hx, hd);
            var tip = new P2(h.X + u.X * w, h.Y + u.Y * w);

            G(GrupoPlanta.Folha, true);
            if (_c.PlantaFolha == FolhaPlanta.Linha) PlanLine(h.X, h.Y, tip.X, tip.Y, false);
            else PlanPoly(new List<P2> { h, tip, new P2(tip.X + v.X * t, tip.Y + v.Y * t), new P2(h.X + v.X * t, h.Y + v.Y * t) }, false);

            // swing: from the tip back to the strike side (closed position)
            var strike = new P2(hx + sx * w, hd);
            bool arcOn = _c.PlantaArco != ArcoPlanta.Nenhum;
            if (_c.PlantaArco == ArcoPlanta.LinhaReta)
            {
                G(GrupoPlanta.Arco, true, true);
                PlanLine(tip.X, tip.Y, strike.X, strike.Y, true);
            }
            else
            {
                G(GrupoPlanta.Arco, arcOn, _c.PlantaArco == ArcoPlanta.ArcoTracejado);
                double m = a / 2;
                PlanArc(tip, strike, new P2(hx + sx * w * Math.Cos(m), hd + sd * w * Math.Sin(m)));
            }

            // closed leaf (dashed), off by default
            G(GrupoPlanta.FolhaFechada, _c.PlantaFolhaFechada, true);
            PlanRect(s.X0, ld0, s.X1, ld1, true);
        }

        /// <summary>Pivot door in plan: leaf open at 90° through the pivot and the two arcs.</summary>
        private void PlanPivot(Rc s, double ld0, double ld1)
        {
            double t = ld1 - ld0, dm = (ld0 + ld1) / 2;
            double px = s.X0 + Math.Min(200, s.W * 0.2);
            double rBig = s.X1 - px, rSmall = px - s.X0;
            G(GrupoPlanta.Folha, true);
            if (_c.PlantaFolha == FolhaPlanta.Linha) PlanLine(px, dm - rSmall, px, dm + rBig, false);
            else PlanRect(px - t / 2, dm - rSmall, px + t / 2, dm + rBig, false);
            double k = Math.Sqrt(0.5);
            G(GrupoPlanta.Arco, _c.PlantaArco != ArcoPlanta.Nenhum, _c.PlantaArco != ArcoPlanta.Arco);
            if (_c.PlantaArco == ArcoPlanta.LinhaReta)
            {
                PlanLine(px, dm + rBig, s.X1, dm, true);
                PlanLine(px, dm - rSmall, s.X0, dm, true);
            }
            else
            {
                PlanArc(new P2(px, dm + rBig), new P2(s.X1, dm), new P2(px + rBig * k, dm + rBig * k));
                PlanArc(new P2(px, dm - rSmall), new P2(s.X0, dm), new P2(px - rSmall * k, dm - rSmall * k));
            }
            G(GrupoPlanta.FolhaFechada, _c.PlantaFolhaFechada, true);
            PlanRect(s.X0, ld0, s.X1, ld1, true);
        }

        // ---------------------------------------------------------------- symbols
        private void Line(double x0, double y0, double x1, double y1, bool dashed)
        {
            if (_c.SimbolosElevacao) Simbolos.Add(new Traco(new P2(x0, y0), new P2(x1, y1), dashed));
        }

        private void Axis(double x0, double y0, double x1, double y1) => Line(x0, y0, x1, y1, true);

        /// <summary>
        /// Hinged leaf (office standard): lines from the two corners on the HINGE side to the
        /// middle of the free (handle) side. Solid; dashed for inward opening only when
        /// "Tracejar quando abre para dentro" is on.
        /// </summary>
        private void HingeSymbol(Rc s, bool hingeLeft, bool inward)
        {
            double hx = hingeLeft ? s.X0 : s.X1, ox = hingeLeft ? s.X1 : s.X0;
            bool dash = inward && _c.TracejarParaDentro;
            Line(hx, s.Y1, ox, s.Ym, dash);
            Line(hx, s.Y0, ox, s.Ym, dash);
        }

        /// <summary>Tilt (tombar): hinge at the bottom, lines from the bottom corners to the top middle.</summary>
        private void TiltSymbol(Rc s)
        {
            bool dash = _c.TracejarParaDentro;
            Line(s.X0, s.Y0, s.Xm, s.Y1, dash);
            Line(s.X1, s.Y0, s.Xm, s.Y1, dash);
        }

        /// <summary>Sliding leaf (office standard): small open triangles ▷ / ◁ / ▷◁ at the leaf centre.</summary>
        private void SlideTriangles(double xm, double ym, double h, int dir)
        {
            double w = h * 0.8;
            void Tri(double apexX, int pointsRight)
            {
                double bx = apexX - pointsRight * w;
                Line(bx, ym + h / 2, apexX, ym, false);
                Line(apexX, ym, bx, ym - h / 2, false);
                Line(bx, ym - h / 2, bx, ym + h / 2, false);
            }
            if (dir > 0) Tri(xm + w / 2, 1);
            else if (dir < 0) Tri(xm - w / 2, -1);
            else { Tri(xm, 1); Tri(xm, -1); }
        }

        /// <summary>Letter "F" (fixo) drawn with lines at the centre of a fixed pane.</summary>
        private void LetterF(Rc r)
        {
            double h = Math.Min(140, Math.Min(r.W, r.H) * 0.25);
            if (h < 30) return;
            double w = h * 0.6, x = r.Xm - w / 2, y0 = r.Ym - h / 2, y1 = r.Ym + h / 2;
            Line(x, y0, x, y1, false);
            Line(x, y1, x + w, y1, false);
            Line(x, r.Ym, x + w * 0.8, r.Ym, false);
        }

        private void Arrow(double xm, double y, double len, int dir)
        {
            double a = xm - len / 2, b = xm + len / 2, head = Math.Min(60, len * 0.25);
            Line(a, y, b, y, false);
            if (dir >= 0) { Line(b, y, b - head, y + head * 0.6, false); Line(b, y, b - head, y - head * 0.6, false); }
            if (dir <= 0) { Line(a, y, a + head, y + head * 0.6, false); Line(a, y, a + head, y - head * 0.6, false); }
        }

        // ---------------------------------------------------------------- hardware
        private double HandleY(Rc s) => _c.IsPorta ? Math.Max(s.Y0 + 100, Math.Min(s.Y1 - 100, 1050)) : s.Ym;

        private void Handle(Rc s, bool left, double d0, double d1)
        {
            if (!Puxadores) return;
            double f = Math.Min(_ff, s.W / 3);
            double x = left ? s.X0 + f / 2 : s.X1 - f / 2;
            VerticalHandle(x, s, d0, d1, HandleY(s));
        }

        private void VerticalHandle(double x, Rc s, double d0, double d1, double y = double.NaN)
        {
            if (double.IsNaN(y)) y = HandleY(s);
            double h = _c.IsPorta ? 160 : 120;
            var r = new Rc(x - 10, y - h / 2, x + 10, y + h / 2);
            Prismas.Add(Prisma.Box(r, d1, d1 + 35, Papel.Puxador, MaterialSlot.Perfil).Lod(false, false, true));
            if (_c.IsPorta) Prismas.Add(Prisma.Box(r, d0 - 35, d0, Papel.Puxador, MaterialSlot.Perfil).Lod(false, false, true));
        }

        private void BottomHandle(Rc s)
        {
            if (!Puxadores) return;
            double f = Math.Min(_ff, s.H / 3);
            var r = new Rc(s.Xm - 60, s.Y0 + f / 2 - 10, s.Xm + 60, s.Y0 + f / 2 + 10);
            Prismas.Add(Prisma.Box(r, SashD1, SashD1 + 35, Papel.Puxador, MaterialSlot.Perfil).Lod(false, false, true));
        }

        private void TopHandle(Rc s)
        {
            if (!Puxadores) return;
            double f = Math.Min(_ff, s.H / 3);
            var r = new Rc(s.Xm - 60, s.Y1 - f / 2 - 10, s.Xm + 60, s.Y1 - f / 2 + 10);
            Prismas.Add(Prisma.Box(r, SashD1, SashD1 + 35, Papel.Puxador, MaterialSlot.Perfil).Lod(false, false, true));
        }

        // ---------------------------------------------------------------- helpers
        /// <summary>
        /// Splits a total among modules: fixed sizes are kept, zeros share what is left. When the
        /// fixed sizes do not fit (or nothing is automatic) they are scaled to the total.
        /// </summary>
        private double[] Distribute(double total, double[] spec, string what)
        {
            int n = spec.Length;
            var result = new double[n];
            if (n == 0) return result;
            double fixedSum = spec.Where(s => s > 0).Sum();
            int autos = spec.Count(s => s <= 0);
            const double min = 80;
            if (autos > 0 && total - fixedSum >= autos * min)
            {
                double each = (total - fixedSum) / autos;
                for (int i = 0; i < n; i++) result[i] = spec[i] > 0 ? spec[i] : each;
                return result;
            }
            if (autos > 0)
            {
                // not enough room: autos get the minimum, fixed ones shrink proportionally
                double room = Math.Max(0, total - autos * min);
                double k = fixedSum > 0 ? room / fixedSum : 0;
                for (int i = 0; i < n; i++) result[i] = spec[i] > 0 ? spec[i] * k : min;
                Avisos.Add($"As medidas das {what} não cabem na esquadria: foram reduzidas proporcionalmente.");
                return Normalize(result, total);
            }
            if (Math.Abs(fixedSum - total) > 0.5)
            {
                double k = total / fixedSum;
                for (int i = 0; i < n; i++) result[i] = spec[i] * k;
                Avisos.Add($"A soma das {what} ({fixedSum:0} mm) difere do total ({total:0} mm): ajustada proporcionalmente. Deixe uma medida vazia para preencher o resto.");
                return result;
            }
            return spec.ToArray();
        }

        private static double[] Normalize(double[] v, double total)
        {
            double s = v.Sum();
            if (s <= 0) return v;
            return v.Select(x => x * total / s).ToArray();
        }

        // ---------------------------------------------------------------- descriptions
        public static string Mm(double v) => v.ToString("0", CultureInfo.InvariantCulture);

        /// <summary>"correr 2 folhas + basculante".</summary>
        public static string ResumoAberturas(EsquadriaConfig c)
        {
            var parts = new List<string>();
            foreach (var l in c.Linhas)
                foreach (var cell in l.Celulas)
                {
                    string s = Catalog.NomeCurto(cell.Abertura);
                    if (cell.Abertura == Abertura.Correr)
                    {
                        int nf = cell.Modo == ModoCorrer.Central && cell.Folhas % 2 == 1 ? cell.Folhas + 1 : cell.Folhas;
                        s += " " + nf + " folhas" + (cell.MistoVeneziana ? " c/ veneziana" : "");
                        if (cell.Modo == ModoCorrer.Central) s += nf >= 4 ? " (abertura central, pontas fixas)" : " (abertura central)";
                        else if (cell.Modo == ModoCorrer.Recolhe) s += " (recolhendo à " + (cell.RecolheDireita ? "direita" : "esquerda") + ", 1 fixa)";
                    }
                    if (cell.Preenchimento == Preenchimento.Veneziana && !(cell.Abertura == Abertura.Correr && cell.MistoVeneziana)) s += " (veneziana)";
                    if (cell.Preenchimento == Preenchimento.Cego) s += " (cega)";
                    if (!parts.Contains(s)) parts.Add(s);
                }
            return string.Join(" + ", parts);
        }

        /// <summary>Technical description written to the family (Description parameter).</summary>
        public static string Descricao(EsquadriaConfig c)
        {
            var perfil = Catalog.Perfil(c.PerfilId);
            var vidro = Catalog.Vidro(c.VidroId);
            var cor = Catalog.Cor(c.CorId);
            string tipo = c.IsPorta ? "Porta" : "Janela";
            string s = $"{tipo} {ResumoAberturas(c)}, {Mm(c.LarguraMm)} x {Mm(c.AlturaMm)} mm";
            if (!c.IsPorta) s += $", peitoril {Mm(c.PeitorilMm)} mm";
            s += $"; {perfil.Material.ToLowerInvariant()} {(cor != null ? cor.Nome.ToLowerInvariant() : "#" + c.CorHex)}";
            if (UsesGlass(c)) s += $"; vidro {vidro.Nome.ToLowerInvariant()}";
            if (!string.IsNullOrWhiteSpace(c.Nome)) s = c.Nome.Trim() + " – " + s;
            return s;
        }

        public static bool UsesGlass(EsquadriaConfig c) =>
            c.Linhas.Any(l => l.Celulas.Any(x => x.Preenchimento == Preenchimento.Vidro || (x.Abertura == Abertura.Correr && x.MistoVeneziana)));
    }
}
