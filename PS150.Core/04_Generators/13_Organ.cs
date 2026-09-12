using System.Collections.Generic;

namespace PS150.Core.Generators
{
    public enum ManualType { Pedal = 0, Manual1 = 1, Manual2 = 2, Manual3 = 3, Manual4 = 4 }

    public class OrganStop
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public ManualType Manual { get; set; }
        public float[] HarmonicWeights { get; set; } = new float[] { 1.0f };
        public int OutputChannelZone { get; set; } = 0; // 0..3 (Dvojice výstupů DAC8x)
        public bool IsActive { get; set; } = false;
    }

    public class CombinationBank
    {
        // Volná kombinace: Mapování ID rejstříku -> Zapnuto/Vypnuto
        public Dictionary<int, bool> StopStates { get; set; } = new();
    }
}