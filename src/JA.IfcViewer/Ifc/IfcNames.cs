using System;
using System.Collections.Generic;

namespace JA.IfcViewer.Ifc
{
    /// <summary>Display names (IfcWallStandardCase from IFCWALLSTANDARDCASE), Portuguese labels and default colours by class.</summary>
    public static class IfcNames
    {
        private static readonly string[] Known =
        {
            "IfcProject","IfcSite","IfcBuilding","IfcBuildingStorey","IfcSpace","IfcZone","IfcSpatialZone","IfcExternalSpatialElement",
            "IfcFacility","IfcFacilityPart","IfcBridge","IfcBridgePart","IfcRoad","IfcRoadPart","IfcRailway","IfcRailwayPart","IfcMarineFacility","IfcMarinePart",
            "IfcWall","IfcWallStandardCase","IfcWallElementedCase","IfcCurtainWall","IfcSlab","IfcSlabStandardCase","IfcSlabElementedCase","IfcRoof",
            "IfcBeam","IfcBeamStandardCase","IfcColumn","IfcColumnStandardCase","IfcMember","IfcMemberStandardCase","IfcPlate","IfcPlateStandardCase",
            "IfcDoor","IfcDoorStandardCase","IfcWindow","IfcWindowStandardCase","IfcOpeningElement","IfcOpeningStandardCase","IfcVoidingFeature",
            "IfcStair","IfcStairFlight","IfcRamp","IfcRampFlight","IfcRailing","IfcCovering","IfcFooting","IfcPile","IfcBuildingElementProxy",
            "IfcBuildingElementPart","IfcElementAssembly","IfcChimney","IfcShadingDevice","IfcFurnishingElement","IfcFurniture","IfcSystemFurnitureElement",
            "IfcTransportElement","IfcVirtualElement","IfcAnnotation","IfcGrid","IfcProxy","IfcGeographicElement","IfcCivilElement",
            "IfcReinforcingBar","IfcReinforcingMesh","IfcTendon","IfcTendonAnchor","IfcMechanicalFastener","IfcFastener","IfcDiscreteAccessory",
            "IfcDistributionElement","IfcDistributionFlowElement","IfcDistributionControlElement","IfcDistributionChamberElement","IfcDistributionPort",
            "IfcFlowSegment","IfcFlowFitting","IfcFlowTerminal","IfcFlowController","IfcFlowMovingDevice","IfcFlowStorageDevice","IfcFlowTreatmentDevice",
            "IfcEnergyConversionDevice","IfcPipeSegment","IfcPipeFitting","IfcDuctSegment","IfcDuctFitting","IfcCableSegment","IfcCableFitting",
            "IfcCableCarrierSegment","IfcCableCarrierFitting","IfcAirTerminal","IfcAirTerminalBox","IfcSanitaryTerminal","IfcWasteTerminal",
            "IfcLightFixture","IfcLamp","IfcOutlet","IfcSwitchingDevice","IfcElectricAppliance","IfcElectricDistributionBoard","IfcJunctionBox",
            "IfcValve","IfcPump","IfcFan","IfcBoiler","IfcChiller","IfcCoil","IfcTank","IfcUnitaryEquipment","IfcFireSuppressionTerminal",
            "IfcSensor","IfcAlarm","IfcController","IfcActuator","IfcFlowInstrument","IfcCommunicationsAppliance","IfcAudioVisualAppliance",
            "IfcMedicalDevice","IfcSpaceHeater","IfcStackTerminal","IfcInterceptor","IfcDamper","IfcFilter","IfcFlowMeter","IfcProtectiveDevice",
            "IfcElectricMotor","IfcElectricGenerator","IfcTransformer","IfcCooledBeam","IfcCoolingTower","IfcHeatExchanger","IfcHumidifier",
            "IfcEvaporator","IfcCondenser","IfcCompressor","IfcBurner","IfcEngine","IfcSolarDevice","IfcElectricFlowStorageDevice","IfcElectricTimeControl",
            "IfcCourse","IfcPavement","IfcKerb","IfcEarthworksFill","IfcEarthworksCut","IfcBearing","IfcDeepFoundation","IfcCaissonFoundation",
            "IfcSign","IfcSignal","IfcTrackElement","IfcRail","IfcMooringDevice","IfcNavigationElement","IfcReinforcedSoil","IfcGeotechnicalStratum",
            "IfcBorehole","IfcAlignment","IfcReferent","IfcPositioningElement","IfcElement","IfcProduct","IfcBuiltElement","IfcFeatureElementSubtraction",
            "IfcProjectionElement","IfcSurfaceFeature","IfcElementComponent","IfcSpatialElement","IfcSpatialStructureElement"
        };

        private static readonly Dictionary<string, string> Map = BuildMap();
        private static Dictionary<string, string> BuildMap()
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string k in Known) d[k.ToUpperInvariant()] = k;
            return d;
        }

        /// <summary>IFCWALLSTANDARDCASE → IfcWallStandardCase.</summary>
        public static string Pretty(string upper)
        {
            if (string.IsNullOrEmpty(upper)) return "";
            if (Map.TryGetValue(upper, out string s)) return s;
            if (upper.StartsWith("IFC") && upper.Length > 3)
                return "Ifc" + upper.Substring(3, 1) + upper.Substring(4).ToLowerInvariant();
            return upper;
        }

        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["IfcWall"] = "Paredes", ["IfcWallStandardCase"] = "Paredes", ["IfcCurtainWall"] = "Fachadas-cortina", ["IfcSlab"] = "Lajes e pisos",
            ["IfcRoof"] = "Coberturas", ["IfcBeam"] = "Vigas", ["IfcColumn"] = "Pilares", ["IfcMember"] = "Perfis", ["IfcPlate"] = "Placas e painéis",
            ["IfcDoor"] = "Portas", ["IfcWindow"] = "Janelas", ["IfcStair"] = "Escadas", ["IfcStairFlight"] = "Lances de escada", ["IfcRamp"] = "Rampas",
            ["IfcRailing"] = "Guarda-corpos", ["IfcCovering"] = "Revestimentos e forros", ["IfcFooting"] = "Fundações", ["IfcPile"] = "Estacas",
            ["IfcBuildingElementProxy"] = "Genéricos", ["IfcFurnishingElement"] = "Mobiliário", ["IfcFurniture"] = "Mobiliário", ["IfcSpace"] = "Ambientes",
            ["IfcSite"] = "Terreno", ["IfcFlowTerminal"] = "Terminais", ["IfcSanitaryTerminal"] = "Louças e metais", ["IfcLightFixture"] = "Luminárias",
            ["IfcPipeSegment"] = "Tubos", ["IfcPipeFitting"] = "Conexões de tubo", ["IfcDuctSegment"] = "Dutos", ["IfcDuctFitting"] = "Conexões de duto",
            ["IfcFlowSegment"] = "Tubos e dutos", ["IfcFlowFitting"] = "Conexões", ["IfcElementAssembly"] = "Conjuntos", ["IfcReinforcingBar"] = "Armaduras",
            ["IfcOpeningElement"] = "Aberturas", ["IfcTransportElement"] = "Elevadores", ["IfcShadingDevice"] = "Brises e sombreamento"
        };

        /// <summary>“Paredes (IfcWall)”-style label for the tree groups.</summary>
        public static string Label(string pretty) => Labels.TryGetValue(pretty, out string l) ? l + " (" + pretty + ")" : pretty;

        public static bool IsSpatial(string upper)
        {
            switch (upper)
            {
                case "IFCPROJECT": case "IFCSITE": case "IFCBUILDING": case "IFCBUILDINGSTOREY": case "IFCSPACE":
                case "IFCFACILITY": case "IFCFACILITYPART": case "IFCBRIDGE": case "IFCBRIDGEPART": case "IFCROAD": case "IFCROADPART":
                case "IFCRAILWAY": case "IFCRAILWAYPART": case "IFCMARINEFACILITY": case "IFCMARINEPART": case "IFCEXTERNALSPATIALELEMENT":
                case "IFCSPATIALZONE":
                    return true;
            }
            return false;
        }

        public static bool IsStoreyLike(string upper) => upper == "IFCBUILDINGSTOREY" || upper == "IFCFACILITYPART" || upper == "IFCBRIDGEPART" || upper == "IFCROADPART" || upper == "IFCRAILWAYPART" || upper == "IFCMARINEPART";

        /// <summary>Default colour (ARGB) when the IFC carries no style: close to what Zoom shows.</summary>
        public static uint DefaultColor(string upper)
        {
            switch (upper)
            {
                case "IFCWALL": case "IFCWALLSTANDARDCASE": case "IFCWALLELEMENTEDCASE": return Argb(255, 222, 220, 212);
                case "IFCSLAB": case "IFCSLABSTANDARDCASE": case "IFCSLABELEMENTEDCASE": return Argb(255, 196, 196, 192);
                case "IFCROOF": return Argb(255, 170, 92, 80);
                case "IFCWINDOW": case "IFCWINDOWSTANDARDCASE": return Argb(110, 150, 190, 225);
                case "IFCPLATE": case "IFCPLATESTANDARDCASE": return Argb(110, 150, 190, 225);
                case "IFCDOOR": case "IFCDOORSTANDARDCASE": return Argb(255, 176, 140, 100);
                case "IFCCOLUMN": case "IFCCOLUMNSTANDARDCASE": case "IFCBEAM": case "IFCBEAMSTANDARDCASE": case "IFCFOOTING": case "IFCPILE":
                    return Argb(255, 182, 182, 190);
                case "IFCMEMBER": case "IFCMEMBERSTANDARDCASE": return Argb(255, 120, 124, 132);
                case "IFCRAILING": return Argb(255, 90, 94, 100);
                case "IFCSTAIR": case "IFCSTAIRFLIGHT": case "IFCRAMP": case "IFCRAMPFLIGHT": return Argb(255, 205, 202, 195);
                case "IFCCOVERING": return Argb(255, 232, 228, 214);
                case "IFCSPACE": return Argb(70, 120, 190, 130);
                case "IFCSITE": case "IFCGEOGRAPHICELEMENT": return Argb(255, 150, 180, 120);
                case "IFCFURNISHINGELEMENT": case "IFCFURNITURE": case "IFCSYSTEMFURNITUREELEMENT": return Argb(255, 200, 170, 130);
                case "IFCOPENINGELEMENT": case "IFCOPENINGSTANDARDCASE": return Argb(60, 255, 140, 40);
                case "IFCBUILDINGELEMENTPROXY": return Argb(255, 200, 200, 205);
                case "IFCTRANSPORTELEMENT": return Argb(255, 150, 150, 160);
                case "IFCREINFORCINGBAR": case "IFCREINFORCINGMESH": return Argb(255, 150, 70, 60);
            }
            if (upper.Contains("PIPE") || upper.Contains("DUCT") || upper.StartsWith("IFCFLOW") || upper.Contains("CABLE")
                || upper.Contains("TERMINAL") || upper.Contains("VALVE") || upper.Contains("PUMP")) return Argb(255, 110, 150, 200);
            return Argb(255, 205, 205, 205);
        }

        public static uint Argb(int a, int r, int g, int b) => ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }
}
