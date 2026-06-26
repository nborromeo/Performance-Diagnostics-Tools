using System;
using System.Collections.Generic;

namespace PerformanceDiagnostics
{
    /// <summary>
    /// Global registry of <see cref="IDiagnosticDetector"/> instances.
    /// Detectors self-register from their <c>[InitializeOnLoad]</c> static constructors.
    /// </summary>
    public static class DiagnosticRegistry
    {
        static readonly List<IDiagnosticDetector> s_Detectors = new List<IDiagnosticDetector>();

        public static IReadOnlyList<IDiagnosticDetector> Detectors => s_Detectors;

        /// <summary>Fires whenever a detector is registered or unregistered.</summary>
        public static event Action DetectorListChanged;

        public static void Register(IDiagnosticDetector detector)
        {
            if (!s_Detectors.Contains(detector))
            {
                s_Detectors.Add(detector);
                DetectorListChanged?.Invoke();
            }
        }

        public static void Unregister(IDiagnosticDetector detector)
        {
            if (s_Detectors.Remove(detector))
                DetectorListChanged?.Invoke();
        }
    }
}
