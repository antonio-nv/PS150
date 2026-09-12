using System.Collections.Generic;
using PS150.Core.Generators;

namespace PS150.Raspi.Hardware
{
    public class OrganHardwareManager
    {
        private readonly List<Mcp23016Controller> _expanders = new();
        private readonly CombinationBank _currentFreeCombination = new();

        public void RegisterExpander(int busId, int address)
        {
            _expanders.Add(new Mcp23016Controller(busId, address));
        }

        public void UpdateHardwareState(List<OrganStop> availableStops)
        {
            // 1. Skenování stisků kláves a sklopek z expandérů
            foreach (var expander in _expanders)
            {
                ushort inputs = expander.ReadKeyInputs();
                // Dekódování bitů na tóny / sklopky
            }

            // 2. Aktualizace LED podsvícení aktivních sklopek
            foreach (var stop in availableStops)
            {
                // Pokud je rejstřík aktivní, rozsvítí se LED podsvícení sklopky přes MCP23016
            }
        }

        public void ApplyFreeCombination(CombinationBank combination, List<OrganStop> stops)
        {
            foreach (var stop in stops)
            {
                if (combination.StopStates.TryGetValue(stop.Id, out bool active))
                {
                    stop.IsActive = active;
                }
            }
        }
    }
}