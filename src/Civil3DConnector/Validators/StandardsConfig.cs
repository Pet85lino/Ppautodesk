// StandardsConfig.cs
// Configuration constants for all Ecuadorian engineering standards used in Civil 3D validation.
// References: NEVI-12-MTOP, NEC-SE-DS, NTE INEN 3054, Normas INTERAGUA 2019, Normas AMAGUA, Ordenanzas Municipales.

using System.Collections.Generic;

namespace Civil3DConnector.Validators
{
    // ─────────────────────────────────────────────────────────────────────────
    // Top-level container – accessed as StandardsConfig.Interagua, etc.
    // ─────────────────────────────────────────────────────────────────────────
    public static class StandardsConfig
    {
        public static readonly InteraguaStandards Interagua = new InteraguaStandards();
        public static readonly AmagualStandards   Amagua    = new AmagualStandards();
        public static readonly MtopStandards       Mtop      = new MtopStandards();
        public static readonly NecStandards         Nec       = new NecStandards();
        public static readonly NteInenStandards     NteInen   = new NteInenStandards();
        public static readonly MunicipalStandards   Municipal = new MunicipalStandards();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // INTERAGUA – Empresa de Agua Potable y Alcantarillado de Guayaquil
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class InteraguaStandards
    {
        // ── Water supply ──────────────────────────────────────────────────────
        public readonly double WaterMinPressure_kPa        = 100.0;
        public readonly double WaterMaxPressure_kPa        = 700.0;
        public readonly double WaterOptimalPressure_kPa    = 200.0;
        public readonly double WaterStaticMaxPressure_kPa  = 800.0;

        public readonly double WaterMinVelocity_ms         = 0.30;
        public readonly double WaterMaxVelocity_ms         = 3.00;
        public readonly double WaterOptimalMinVelocity_ms  = 0.60;
        public readonly double WaterOptimalMaxVelocity_ms  = 1.50;

        public readonly double WaterMinDiameter_Residential_mm   = 75;
        public readonly double WaterMinDiameter_Commercial_mm    = 100;
        public readonly double WaterMinDiameter_Industrial_mm    = 150;
        public readonly double WaterMinDiameter_Transmission_mm  = 200;
        public readonly double WaterMinDiameter_FireFighting_mm  = 100;

        // Cover depths – water
        public readonly double WaterCoverPedestrian_Min_m    = 0.80;
        public readonly double WaterCoverPedestrian_Rec_m    = 1.00;
        public readonly double WaterCoverVehicle_Min_m       = 1.00;
        public readonly double WaterCoverVehicle_Rec_m       = 1.20;
        public readonly double WaterCoverHeavyTraffic_m      = 1.50;
        public readonly double WaterCoverAgricultural_Min_m  = 1.20;
        public readonly double WaterCoverRocky_Min_m         = 0.60;

        // ── Sewerage (alcantarillado sanitario) ───────────────────────────────
        public readonly double SewerMinVelocity_ms           = 0.60;   // self-cleaning
        public readonly double SewerMaxVelocity_ms           = 5.00;
        public readonly double SewerMaxVelocityAbrasive_ms   = 3.00;
        public readonly double SewerManningN_PVC             = 0.011;
        public readonly double SewerManningN_Concrete        = 0.013;
        public readonly double SewerFlowDepthRatio_Max       = 0.75;

        // Minimum diameters – sewer
        public readonly double SewerMinDiameter_Lateral_mm    = 160;
        public readonly double SewerMinDiameter_Collector_mm  = 200;
        public readonly double SewerMinDiameter_Secondary_mm  = 250;
        public readonly double SewerMinDiameter_Primary_mm    = 400;
        public readonly double SewerMinDiameter_Interceptor_mm = 600;

        // Minimum slopes (S_min = K / sqrt(D)) – stored as percent
        // Key = nominal diameter in mm, Value = minimum slope in percent
        public readonly IReadOnlyDictionary<int, double> SewerMinSlope_Percent =
            new Dictionary<int, double>
            {
                { 160,  0.50 },
                { 200,  0.40 },
                { 250,  0.30 },
                { 300,  0.25 },
                { 350,  0.20 },
                { 400,  0.18 },
                { 450,  0.15 },
                { 500,  0.12 },
                { 600,  0.10 },
                { 700,  0.08 },
                { 800,  0.07 },
                { 900,  0.06 },
                { 1000, 0.05 },
                { 1200, 0.04 },
                { 1500, 0.03 }
            };

        public readonly double SewerMaxSlope_Percent                   = 15.0;
        public readonly double SewerMaxSlopeWithEnergyDissipator_Percent = 30.0;

        // Cover depths – sewer
        public readonly double SewerCoverUnderPavement_Min_m    = 1.20;
        public readonly double SewerCoverUnderSidewalk_Min_m    = 0.80;
        public readonly double SewerCoverUnderUnpaved_Min_m     = 1.00;
        public readonly double SewerCoverMax_m                  = 6.00;
        public readonly double SewerCoverMaxSpecial_m           = 10.00;
        public readonly double SewerCoverCrossWater_Min_m       = 0.30;

        // Manholes
        public readonly double ManholeSpacingMax_m              = 120.0;
        public readonly double ManholeSpacingMaxLargeDia_m      = 200.0;
        public readonly double ManholeInternalDiameter_mm       = 1200;
        public readonly double ManholeInternalDiameterDeep_mm   = 1500;
        public readonly double ManholeDropRequired_m            = 0.60;

        // Network separations
        public readonly double SeparationWaterToSewer_H_m       = 3.00;
        public readonly double SeparationWaterToSewer_V_m       = 0.30;
        public readonly double SeparationWaterToGas_H_m         = 0.60;
        public readonly double SeparationWaterToElectric_H_m    = 0.60;
        public readonly double SeparationSewerToBuilding_m      = 3.00;
        public readonly double SeparationWaterToBuilding_m      = 1.50;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AMAGUA – Empresa Pública Metropolitana de Agua Potable y Saneamiento de Quito
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class AmagualStandards
    {
        // Water supply – similar to Interagua with Quito-specific adjustments
        public readonly double WaterMinPressure_kPa            = 98.1;   // 10 mca
        public readonly double WaterMaxPressure_kPa            = 686.7;  // 70 mca
        public readonly double WaterStaticMaxPressure_kPa      = 784.8;  // 80 mca

        public readonly double WaterMinVelocity_ms             = 0.30;
        public readonly double WaterMaxVelocity_ms             = 2.50;

        // Minimum diameters
        public readonly double WaterMinDiameter_Residential_mm = 75;
        public readonly double WaterMinDiameter_Commercial_mm  = 100;
        public readonly double WaterMinDiameter_Distribution_mm = 100;
        public readonly double WaterMinDiameter_Transmission_mm = 200;

        // Cover depths (Quito has volcanic soils and earthquake risk)
        public readonly double WaterCoverVehicle_Min_m         = 1.00;
        public readonly double WaterCoverVehicle_Rec_m         = 1.20;
        public readonly double WaterCoverSeismic_Zone_Min_m    = 1.20;   // NEC Zone V (Quito)

        // Sewerage
        public readonly double SewerMinVelocity_ms             = 0.60;
        public readonly double SewerMaxVelocity_ms             = 5.00;
        public readonly double SewerManningN_PVC               = 0.011;
        public readonly double SewerManningN_Concrete          = 0.013;

        public readonly double SewerMinDiameter_Lateral_mm     = 200;    // Quito requires 200 minimum
        public readonly double SewerMinDiameter_Collector_mm   = 250;

        // Slopes (%)
        public readonly IReadOnlyDictionary<int, double> SewerMinSlope_Percent =
            new Dictionary<int, double>
            {
                { 200,  0.40 },
                { 250,  0.30 },
                { 300,  0.25 },
                { 350,  0.22 },
                { 400,  0.18 },
                { 500,  0.12 },
                { 600,  0.10 },
                { 800,  0.07 },
                { 1000, 0.05 },
                { 1200, 0.04 }
            };

        public readonly double SewerMaxSlope_Percent           = 15.0;

        // Cover depths
        public readonly double SewerCoverMin_m                 = 1.20;
        public readonly double SewerCoverMax_m                 = 6.00;

        // Network separations (same as national)
        public readonly double SeparationWaterToSewer_H_m      = 3.00;
        public readonly double SeparationWaterToSewer_V_m      = 0.30;

        // Pumping stations
        public readonly double PumpStationWetWell_MinCapacity_min = 15.0;
        public readonly double PumpStationStandbyRequired       = 1.0;   // 1 standby pump always required
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MTOP – Ministerio de Transporte y Obras Públicas
    // (NEVI-12 road geometry design standards)
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MtopStandards
    {
        // ── Design speeds ──────────────────────────────────────────────────
        public readonly int DesignSpeed_E_Max_kph    = 120;
        public readonly int DesignSpeed_E_Min_kph    = 80;
        public readonly int DesignSpeed_R1_Max_kph   = 100;
        public readonly int DesignSpeed_R1_Min_kph   = 60;
        public readonly int DesignSpeed_R2_Max_kph   = 80;
        public readonly int DesignSpeed_R2_Min_kph   = 40;
        public readonly int DesignSpeed_R3_Max_kph   = 40;
        public readonly int DesignSpeed_R3_Min_kph   = 20;

        // ── Lane widths (m) ────────────────────────────────────────────────
        public readonly double LaneWidth_E_m         = 3.65;
        public readonly double LaneWidth_R1_m        = 3.65;
        public readonly double LaneWidth_R2_m        = 3.50;
        public readonly double LaneWidth_R3_m        = 3.00;
        public readonly double LaneWidth_Urban_A_m   = 3.50;
        public readonly double LaneWidth_Urban_C_m   = 3.30;
        public readonly double LaneWidth_Urban_L_m   = 3.00;
        public readonly double LaneWidth_Min_m        = 3.00;

        // ── Shoulder widths (m) ────────────────────────────────────────────
        public readonly double ShoulderWidth_E_Paved_m    = 3.00;
        public readonly double ShoulderWidth_R1_Paved_m   = 1.50;
        public readonly double ShoulderWidth_R2_Paved_m   = 1.00;
        public readonly double ShoulderWidth_R3_Unpaved_m = 1.00;

        // ── Minimum horizontal curve radii (m) ────────────────────────────
        // Key = design speed (km/h), Value = absolute minimum radius (m)
        public readonly IReadOnlyDictionary<int, double> MinCurveRadius_m =
            new Dictionary<int, double>
            {
                { 20,  10 },
                { 30,  15 },
                { 40,  25 },
                { 50,  45 },
                { 60,  75 },
                { 70,  115 },
                { 80,  180 },
                { 90,  275 },
                { 100, 395 },
                { 110, 510 },
                { 120, 670 }
            };

        // Desirable minimum radii (km/h → m)
        public readonly IReadOnlyDictionary<int, double> DesirableMinCurveRadius_m =
            new Dictionary<int, double>
            {
                { 20,  20 },
                { 30,  30 },
                { 40,  50 },
                { 50,  80 },
                { 60,  130 },
                { 70,  195 },
                { 80,  290 },
                { 90,  420 },
                { 100, 560 },
                { 110, 700 },
                { 120, 900 }
            };

        // ── Maximum longitudinal grades (%) ───────────────────────────────
        // Key = design speed, inner dict = terrain type → max grade %
        public readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, double>> MaxGrade_Percent =
            new Dictionary<int, IReadOnlyDictionary<string, double>>
            {
                { 20,  new Dictionary<string,double> { {"P",10},{"O",12},{"M",15},{"E",18} } },
                { 30,  new Dictionary<string,double> { {"P",9}, {"O",11},{"M",14},{"E",16} } },
                { 40,  new Dictionary<string,double> { {"P",7}, {"O",9}, {"M",12},{"E",14} } },
                { 50,  new Dictionary<string,double> { {"P",6}, {"O",8}, {"M",11},{"E",13} } },
                { 60,  new Dictionary<string,double> { {"P",5}, {"O",7}, {"M",10},{"E",12} } },
                { 70,  new Dictionary<string,double> { {"P",5}, {"O",7}, {"M",9}, {"E",11} } },
                { 80,  new Dictionary<string,double> { {"P",4}, {"O",6}, {"M",8}, {"E",10} } },
                { 90,  new Dictionary<string,double> { {"P",3}, {"O",5}, {"M",7}, {"E",9}  } },
                { 100, new Dictionary<string,double> { {"P",3}, {"O",4}, {"M",6}, {"E",8}  } },
                { 110, new Dictionary<string,double> { {"P",3}, {"O",4}, {"M",5}, {"E",7}  } },
                { 120, new Dictionary<string,double> { {"P",3}, {"O",4}, {"M",5}, {"E",6}  } }
            };

        public readonly double MinGrade_Drainage_Percent     = 0.50;
        public readonly double RecommendedMinGrade_Percent    = 1.00;
        public readonly double AbsoluteMaxGrade_Percent       = 18.0;

        // ── Superelevation ─────────────────────────────────────────────────
        public readonly double MaxSuperelevation_Percent      = 8.0;
        public readonly double MaxSuperelevation_Urban_Percent = 6.0;
        public readonly double MaxSuperelevation_SnowIce_Percent = 6.0;
        public readonly double NormalCrownSlope_Percent       = 2.0;
        public readonly double MinSuperelevation_Percent      = 2.0;

        // ── Stopping sight distances (m) ─────────────────────────────────
        public readonly IReadOnlyDictionary<int, double> StoppingSightDistance_m =
            new Dictionary<int, double>
            {
                { 20,  15 },
                { 30,  25 },
                { 40,  40 },
                { 50,  55 },
                { 60,  75 },
                { 70,  100 },
                { 80,  130 },
                { 90,  160 },
                { 100, 185 },
                { 110, 240 },
                { 120, 285 }
            };

        // ── Passing sight distances (m) ───────────────────────────────────
        public readonly IReadOnlyDictionary<int, double> PassingSightDistance_m =
            new Dictionary<int, double>
            {
                { 40,  170 },
                { 50,  215 },
                { 60,  255 },
                { 70,  280 },
                { 80,  350 },
                { 90,  415 },
                { 100, 485 },
                { 110, 560 },
                { 120, 670 }
            };

        // ── Vertical curve K values (L = K * A, A = grade change %) ─────
        public readonly IReadOnlyDictionary<int, double> KValue_Crest =
            new Dictionary<int, double>
            {
                { 20,  1  },
                { 30,  2  },
                { 40,  4  },
                { 50,  7  },
                { 60,  11 },
                { 70,  19 },
                { 80,  30 },
                { 90,  52 },
                { 100, 84 },
                { 110, 110 },
                { 120, 160 }
            };

        public readonly IReadOnlyDictionary<int, double> KValue_Sag =
            new Dictionary<int, double>
            {
                { 20,  2  },
                { 30,  4  },
                { 40,  6  },
                { 50,  8  },
                { 60,  11 },
                { 70,  14 },
                { 80,  20 },
                { 90,  25 },
                { 100, 30 },
                { 110, 40 },
                { 120, 50 }
            };

        // ── Intersection geometry ─────────────────────────────────────────
        public readonly double Intersection_MinAngle_degrees    = 60.0;
        public readonly double Intersection_PreferredAngle_deg  = 90.0;
        public readonly double CornerRadius_Local_m             = 6.0;
        public readonly double CornerRadius_Collector_m         = 9.0;
        public readonly double CornerRadius_Arterial_m          = 12.0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NEC – Norma Ecuatoriana de la Construcción
    // (Structural and seismic requirements)
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class NecStandards
    {
        // ── Seismic zones (NEC-SE-DS) ─────────────────────────────────────
        // Zone I = lowest seismicity, Zone VI = highest
        public readonly IReadOnlyDictionary<string, SeismicZoneData> SeismicZones =
            new Dictionary<string, SeismicZoneData>
            {
                { "I",   new SeismicZoneData("I",   0.15, "Galápagos (offshore),  muy baja sismicidad") },
                { "II",  new SeismicZoneData("II",  0.25, "Amazonía interior, baja sismicidad") },
                { "III", new SeismicZoneData("III", 0.30, "Sierra central, sismicidad moderada") },
                { "IV",  new SeismicZoneData("IV",  0.35, "Sierra norte, sismicidad alta") },
                { "V",   new SeismicZoneData("V",   0.40, "Quito, Guayaquil, Costa, sismicidad muy alta") },
                { "VI",  new SeismicZoneData("VI",  0.50, "Litoral norte, sismicidad extrema") }
            };

        // Peak Ground Acceleration by zone
        public readonly IReadOnlyDictionary<string, double> PGA_z =
            new Dictionary<string, double>
            {
                { "I", 0.15 }, { "II", 0.25 }, { "III", 0.30 },
                { "IV", 0.35 }, { "V", 0.40 }, { "VI", 0.50 }
            };

        // Representative cities and their seismic zone
        public readonly IReadOnlyDictionary<string, string> CitySeismicZone =
            new Dictionary<string, string>
            {
                { "Quito",          "V"   },
                { "Guayaquil",      "V"   },
                { "Cuenca",         "IV"  },
                { "Ambato",         "V"   },
                { "Riobamba",       "IV"  },
                { "Loja",           "IV"  },
                { "Manta",          "V"   },
                { "Portoviejo",     "V"   },
                { "Esmeraldas",     "VI"  },
                { "Santo_Domingo",  "V"   },
                { "Machala",        "V"   },
                { "Ibarra",         "V"   },
                { "Latacunga",      "V"   },
                { "Tulcan",         "V"   }
            };

        // ── Soil site classification (NEC-SE-DS Table 3.3) ────────────────
        public readonly IReadOnlyDictionary<string, string> SoilTypeDescription =
            new Dictionary<string, string>
            {
                { "A", "Roca rígida, Vs30 > 1500 m/s" },
                { "B", "Roca de rigidez media, 760 ≤ Vs30 ≤ 1500 m/s" },
                { "C", "Perfiles de suelo muy denso o roca blanda, 360 ≤ Vs30 < 760 m/s" },
                { "D", "Perfiles de suelo rígido, 180 ≤ Vs30 < 360 m/s" },
                { "E", "Perfil que cumple Vs30 < 180 m/s o con capa de arcilla blanda" },
                { "F", "Suelos que requieren evaluación específica" }
            };

        // ── Design return periods ─────────────────────────────────────────
        public readonly int ReturnPeriod_ServiceLimit_years     = 72;    // 50% in 50 years
        public readonly int ReturnPeriod_UltimateLimit_years    = 475;   // 10% in 50 years
        public readonly int ReturnPeriod_CollapseLimit_years    = 2475;  // 2%  in 50 years

        // ── Structural importance factors (I) ─────────────────────────────
        public readonly double ImportanceFactor_Critical        = 1.50;  // hospitals, firehouses
        public readonly double ImportanceFactor_Essential       = 1.25;  // schools, gov buildings
        public readonly double ImportanceFactor_Normal          = 1.00;  // residential, commercial
        public readonly double ImportanceFactor_Minor           = 0.80;  // warehouses, low occupancy

        // ── Pipe design under seismic conditions ─────────────────────────
        public readonly double SeismicCoverIncrease_ZoneV_m     = 0.20;  // additional cover in zone V+
        public readonly bool   HDPERequiredForSeismicCrossings  = true;
        public readonly double SeismicJointFlexibility_mm       = 50;    // min axial joint movement

        // ── Concrete design (NEC-SE-HM) ───────────────────────────────────
        public readonly double MinConcreteStrength_Structural_MPa = 21.0; // f'c = 210 kg/cm2
        public readonly double MinConcreteStrength_Footings_MPa   = 21.0;
        public readonly double MinConcreteStrength_Columns_MPa    = 24.0;
        public readonly double MinConcreteStrength_Walls_MPa      = 21.0;
        public readonly double MinSteelYield_MPa                  = 420.0; // fy = 4200 kg/cm2

        // ── Foundation minimum depths ─────────────────────────────────────
        public readonly double MinFoundationDepth_General_m       = 0.60;
        public readonly double MinFoundationDepth_Frost_m         = 0.90;
        public readonly double MinFoundationDepth_Adjacent_m      = 1.50;
    }

    public sealed class SeismicZoneData
    {
        public readonly string Zone;
        public readonly double PeakGroundAcceleration_g;
        public readonly string Description;

        public SeismicZoneData(string zone, double pga, string description)
        {
            Zone = zone;
            PeakGroundAcceleration_g = pga;
            Description = description;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // NTE INEN – Instituto Ecuatoriano de Normalización (applicable standards)
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class NteInenStandards
    {
        // INEN 1373 – PVC pressure pipes for water supply
        public readonly string INEN1373_Title         = "Tubería de PVC-U para presión";
        public readonly double[] INEN1373_ValidDiameters_mm = { 25, 32, 40, 50, 63, 75, 90, 110, 140, 160, 200, 250, 315, 400, 500, 630 };
        public readonly double INEN1373_MinHazenWilliams = 150;

        // INEN 3054 – PVC sewer pipes
        public readonly string INEN3054_Title         = "Tubería de PVC-U estructurado para alcantarillado";
        public readonly string[] INEN3054_StiffnessClasses = { "SN2", "SN4", "SN8" };
        public readonly double[] INEN3054_ValidDiameters_mm = { 160, 200, 250, 315, 400, 500, 630, 800, 1000 };

        // INEN 1374 – HDPE pipes for water supply
        public readonly string INEN1374_Title         = "Tubería de polietileno de alta densidad (PEAD) para agua potable";
        public readonly string[] INEN1374_PressureClasses = { "PE80 PN6", "PE80 PN10", "PE100 PN12.5", "PE100 PN16" };

        // INEN 004 – Road signs and horizontal markings
        public readonly string INEN004_Title          = "Señalización vial: Señales y Marcas viales";
        public readonly double SignPost_MinHeight_m    = 1.80;  // clearance to lowest point of sign
        public readonly double SignPost_LateralOffset_m = 0.60; // min from edge of pavement

        // INEN 1787 – Cement for general use
        public readonly string INEN1787_Title         = "Cementos: Requisitos";
        public readonly double Cement_MinStrength_28day_MPa = 32.5;

        // INEN 2496 – Manholes and inspection chambers
        public readonly string INEN2496_Title         = "Pozos de revisión prefabricados de concreto";
        public readonly double Manhole_LoadClass_kN   = 400;    // D400 class
        public readonly double Manhole_MinDiameter_mm = 600;    // cover opening

        // Hydraulic concrete pipes
        public readonly string INEN1332_Title         = "Tubos de concreto simple o reforzado para alcantarillado";
        public readonly double[] INEN1332_ValidDiameters_mm = { 200, 250, 300, 375, 450, 525, 600, 750, 900, 1050, 1200 };
        public readonly double INEN1332_Manning_n     = 0.013;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Municipal Ordinances – Quito, Guayaquil, Cuenca
    // ─────────────────────────────────────────────────────────────────────────
    public sealed class MunicipalStandards
    {
        public readonly QuitoOrdinance Quito       = new QuitoOrdinance();
        public readonly GuayaquilOrdinance Guayaquil = new GuayaquilOrdinance();
        public readonly CuencaOrdinance Cuenca     = new CuencaOrdinance();
    }

    public sealed class QuitoOrdinance
    {
        public readonly string Reference          = "Ordenanza Municipal 3746 y PUOS Quito 2023";
        public readonly string SeismicZone        = "V";

        // Urban road standards (Quito)
        public readonly double ArterialMinWidth_m  = 18.0;   // right-of-way
        public readonly double CollectorMinWidth_m = 14.0;
        public readonly double LocalMinWidth_m     = 10.0;
        public readonly double AlleyMinWidth_m     = 6.0;

        // Sidewalk widths
        public readonly double SidewalkMin_Arterial_m   = 3.00;
        public readonly double SidewalkMin_Collector_m  = 2.00;
        public readonly double SidewalkMin_Local_m      = 1.50;

        // Setbacks
        public readonly double SetbackFront_m      = 5.0;
        public readonly double SetbackSide_m       = 3.0;
        public readonly double SetbackRear_m       = 3.0;

        // Water/sewer for Quito (EPMAPS / AMAGUA)
        public readonly double SewerMinDiameter_mm = 200;
        public readonly double WaterMinDiameter_mm = 75;
        public readonly double SewerMinCover_m     = 1.20;
        public readonly double WaterMinCover_m     = 1.00;
        public readonly double SeparationWaterSewer_H_m = 3.00;

        // Land use density zones
        public readonly IReadOnlyDictionary<string, double> MaxFloorAreaRatio =
            new Dictionary<string, double>
            {
                { "ZR1", 0.80 },  // suburban residential
                { "ZR2", 1.60 },  // medium density residential
                { "ZC1", 4.00 },  // commercial
                { "ZM1", 8.00 }   // mixed use / high density
            };
    }

    public sealed class GuayaquilOrdinance
    {
        public readonly string Reference           = "Ordenanza Municipal de Guayaquil - Normas de Urbanización";
        public readonly string SeismicZone         = "V";

        // Urban road standards (Guayaquil)
        public readonly double ArterialMinWidth_m   = 20.0;
        public readonly double CollectorMinWidth_m  = 16.0;
        public readonly double LocalMinWidth_m      = 12.0;

        // Sidewalk widths
        public readonly double SidewalkMin_Arterial_m   = 3.00;
        public readonly double SidewalkMin_Collector_m  = 2.50;
        public readonly double SidewalkMin_Local_m      = 1.80;

        // Water/sewer for Guayaquil (INTERAGUA)
        public readonly double SewerMinDiameter_mm  = 200;
        public readonly double WaterMinDiameter_mm  = 75;
        public readonly double SewerMinCover_m      = 1.20;
        public readonly double WaterMinCover_m      = 1.00;
        public readonly double SeparationWaterSewer_H_m = 3.00;

        // Flood-specific (Guayaquil is low-lying, flood risk high)
        public readonly double MinFloorElevation_AboveStreet_m  = 0.30;
        public readonly double DrainageDesignReturn_years        = 5;
        public readonly double SeaLevelRise_Allowance_m          = 0.30;
    }

    public sealed class CuencaOrdinance
    {
        public readonly string Reference           = "Ordenanza de Urbanización del Cantón Cuenca 2022";
        public readonly string SeismicZone         = "IV";
        public readonly string UtilityAuthority    = "ETAPA EP";

        // Urban road standards (Cuenca)
        public readonly double ArterialMinWidth_m   = 18.0;
        public readonly double CollectorMinWidth_m  = 14.0;
        public readonly double LocalMinWidth_m      = 10.0;
        public readonly double PedestrianStreet_m   = 6.0;

        // Heritage zone restrictions
        public readonly bool HeritageZoneRequiresCulturalApproval = true;
        public readonly string HeritageZoneAuthority = "Instituto Nacional de Patrimonio Cultural";

        // Water/sewer for Cuenca (ETAPA EP)
        public readonly double SewerMinDiameter_mm  = 200;
        public readonly double WaterMinDiameter_mm  = 75;
        public readonly double SewerMinCover_m      = 1.20;
        public readonly double WaterMinCover_m      = 1.00;
        public readonly double SeparationWaterSewer_H_m = 3.00;

        // Environmental (Cuenca – UNESCO heritage, river protection)
        public readonly double RiverSetback_m       = 30.0;   // Tomebamba, Yanuncay, etc.
        public readonly double WetlandSetback_m     = 50.0;
    }
}
