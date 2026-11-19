using System.Collections.Generic;
using UnityEngine;

namespace AkaitoAi.UniversalMountSystem
{
    /// <summary>
    /// Capability interface for any target that can be physically mounted or boarded by a character
    /// (cars, motorcycles, horses, boats, aircraft, mechs, turrets, etc.).
    /// Supports any number of seats per mount.
    /// </summary>
    public interface IMountable : IControlCapability
    {
        IReadOnlyList<MountSeat> Seats { get; }
        MountSeat PrimarySeat { get; }
        ExitSafetyPolicy ExitPolicy { get; }
        /// <summary>Per-mount boarding speed limit in km/h. Negative values inherit MountSessionSettingsSO.maxBoardingSpeedKmh.</summary>
        float MaxBoardingSpeedKmh { get; }
        /// <summary>Per-mount exit speed limit in km/h. Negative values inherit MountSessionSettingsSO.maxExitSpeedKmh.</summary>
        float MaxExitSpeedKmh { get; }

        MountSeat FindAvailableSeat(Vector3 characterPosition);
        bool CanMount(GameObject character, out string reason);
        bool CanExit(GameObject character, out string reason);

        bool ReserveSeat(MountSeat seat, GameObject character, long transactionId);
        void ReleaseSeatReservation(MountSeat seat, long transactionId);

        void OccupySeat(MountSeat seat, GameObject character);
        void VacateSeat(MountSeat seat, GameObject character);
    }

    /// <summary>
    /// Global zero-allocation registry for active mountable control targets.
    /// Allows any number of mounts of any type to be discovered automatically by interaction prompts
    /// and mount session managers without concrete type dependencies or FindObjectsOfType calls.
    /// </summary>
    public static class MountableRegistry
    {
        private static readonly List<IControlTarget> activeMountables = new List<IControlTarget>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatic()
        {
            activeMountables.Clear();
        }

        public static IReadOnlyList<IControlTarget> ActiveMountables => activeMountables;

        public static void Register(IControlTarget target)
        {
            if (target != null && !activeMountables.Contains(target))
            {
                activeMountables.Add(target);
            }
        }

        public static void Unregister(IControlTarget target)
        {
            if (target != null)
            {
                activeMountables.Remove(target);
            }
        }

        public static void Clear()
        {
            activeMountables.Clear();
        }
    }
}
